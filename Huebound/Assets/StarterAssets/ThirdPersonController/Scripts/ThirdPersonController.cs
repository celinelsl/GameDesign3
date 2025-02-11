using System.Collections;
using UnityEngine;
using DG.Tweening;          // For DOTween effects
using Cinemachine;          // For Cinemachine and impulse
#if ENABLE_INPUT_SYSTEM 
using UnityEngine.InputSystem;
#endif

namespace StarterAssets
{
    [RequireComponent(typeof(CharacterController))]
#if ENABLE_INPUT_SYSTEM 
    [RequireComponent(typeof(PlayerInput))]
#endif
    public class ThirdPersonController : MonoBehaviour
    {
        #region Movement & Jumping Variables

        [Header("Player")]
        [Tooltip("Move speed of the character in m/s")]
        public float MoveSpeed = 2.0f;
        [Tooltip("Sprint speed of the character in m/s")]
        public float SprintSpeed = 5.335f;
        [Tooltip("How fast the character turns to face movement direction")]
        [Range(0.0f, 0.3f)]
        public float RotationSmoothTime = 0.12f;
        [Tooltip("Acceleration and deceleration")]
        public float SpeedChangeRate = 10.0f;

        public AudioClip LandingAudioClip;
        public AudioClip[] FootstepAudioClips;
        [Range(0, 1)] public float FootstepAudioVolume = 0.5f;

        [Space(10)]
        [Tooltip("The height the player can jump")]
        public float JumpHeight = 1.2f;
        [Tooltip("The character uses its own gravity value. The engine default is -9.81f")]
        public float Gravity = -15.0f;

        [Space(10)]
        [Tooltip("Time required to pass before being able to jump again. Set to 0f to instantly jump again")]
        public float JumpTimeout = 0.50f;
        [Tooltip("Time required to pass before entering the fall state. Useful for walking down stairs")]
        public float FallTimeout = 0.15f;

        [Header("Player Grounded")]
        [Tooltip("If the character is grounded or not. Not part of the CharacterController built in grounded check")]
        public bool Grounded = true;
        [Tooltip("Useful for rough ground")]
        public float GroundedOffset = -0.14f;
        [Tooltip("The radius of the grounded check. Should match the radius of the CharacterController")]
        public float GroundedRadius = 0.28f;
        [Tooltip("What layers the character uses as ground")]
        public LayerMask GroundLayers;

        #endregion

        #region Cinemachine Settings

        [Header("Cinemachine")]
        [Tooltip("The follow target set in the Cinemachine Virtual Camera that the camera will follow")]
        public GameObject CinemachineCameraTarget;
        [Tooltip("How far in degrees can you move the camera up")]
        public float TopClamp = 70.0f;
        [Tooltip("How far in degrees can you move the camera down")]
        public float BottomClamp = -30.0f;
        [Tooltip("Additional degress to override the camera. Useful for fine tuning camera position when locked")]
        public float CameraAngleOverride = 0.0f;
        [Tooltip("For locking the camera position on all axis")]
        public bool LockCameraPosition = false;

        // Cinemachine internal variables.
        private float _cinemachineTargetYaw;
        private float _cinemachineTargetPitch;

        #endregion

        #region Internal Movement Variables

        private float _speed;
        private float _animationBlend;
        private float _targetRotation = 0.0f;
        private float _rotationVelocity;
        private float _verticalVelocity;
        private float _terminalVelocity = 53.0f;

        private float _jumpTimeoutDelta;
        private float _fallTimeoutDelta;

        // Animation IDs.
        private int _animIDSpeed;
        private int _animIDGrounded;
        private int _animIDJump;
        private int _animIDFreeFall;
        private int _animIDMotionSpeed;
        private int _animIDShooting;

#if ENABLE_INPUT_SYSTEM 
        private PlayerInput _playerInput;
#endif
        private Animator _animator;
        private CharacterController _controller;
        private StarterAssetsInputs _input;
        private Camera _mainCamera;

        private InputAction shootAction;
        private const float _threshold = 0.01f;
        private bool _hasAnimator;

        #endregion

        #region Shooting Gameplay Variables

        // Bullet instantiation.
        public GameObject bulletPrefab;
        public Transform gunPoint;
        private bool isShooting;
        [SerializeField] private float fireRate = 0.05f; // Adjust fire rate as needed.
        private Coroutine shootingCoroutine;

        #endregion

        #region Shooting Visual Polish Variables

        // The following fields are taken from the separate shooting system.
        [Header("Shooting Visuals")]
        [SerializeField] private ParticleSystem inkParticle;
        [SerializeField] private Transform parentController;
        [SerializeField] private Transform splatGunNozzle;
        [SerializeField] private CinemachineFreeLook freeLookCamera;
        private CinemachineImpulseSource impulseSource;

        // New field to control the rotation speed of the parent controller.
        [SerializeField] private float desiredRotationSpeed = 0.1f;

        #endregion

        private bool IsCurrentDeviceMouse
        {
            get
            {
#if ENABLE_INPUT_SYSTEM
                return _playerInput.currentControlScheme == "KeyboardMouse";
#else
                return false;
#endif
            }
        }

        #region MonoBehaviour Methods

        private void Awake()
        {
            _mainCamera = Camera.main;
            _controller = GetComponent<CharacterController>();
            _input = GetComponent<StarterAssetsInputs>();
#if ENABLE_INPUT_SYSTEM 
            _playerInput = GetComponent<PlayerInput>();
            shootAction = _playerInput.actions["Fire"];
#else
            Debug.LogError("Starter Assets package is missing dependencies. Please use Tools/Starter Assets/Reinstall Dependencies to fix it");
#endif
        }

        private void OnEnable()
        {
            shootAction.Enable();
            shootAction.performed += _ => StartShooting();
            shootAction.canceled += _ => StopShooting();
        }

        private void OnDisable()
        {
            shootAction.Disable();
            shootAction.performed -= _ => StartShooting();
            shootAction.canceled -= _ => StopShooting();
        }

        private void Start()
        {
            _mainCamera = Camera.main;
            _cinemachineTargetYaw = CinemachineCameraTarget.transform.rotation.eulerAngles.y;
            _hasAnimator = TryGetComponent(out _animator);
            AssignAnimationIDs();
            _jumpTimeoutDelta = JumpTimeout;
            _fallTimeoutDelta = FallTimeout;

            if (freeLookCamera != null)
                impulseSource = freeLookCamera.GetComponent<CinemachineImpulseSource>();
        }

        private void Update()
        {
            JumpAndGravity();
            GroundedCheck();
            Move();

            // Using isShooting directly as our "pressing" indicator.
            bool pressing = isShooting;

            if (pressing)
            {
                VisualPolish();
                // Optionally, remove or adjust this call if it overrides your x-axis changes:
                // RotateParentToCamera();
            }

            if (parentController != null)
            {
                Vector3 angle = parentController.localEulerAngles;
                // Use the updated camera pitch (_cinemachineTargetPitch) instead of freeLookCamera.m_YAxis.Value.
                float normalizedPitch = Mathf.InverseLerp(BottomClamp, TopClamp, _cinemachineTargetPitch);
                float targetX = pressing ? RemapCamera(normalizedPitch, 0, 1, -25, 25) : 0;
                parentController.localEulerAngles = new Vector3(
                    Mathf.LerpAngle(angle.x, targetX, 0.3f),
                    angle.y,
                    angle.z);
            }
        }

        private void LateUpdate()
        {
            CameraRotation();
        }

        #endregion

        #region Movement & Rotation Methods

        private void AssignAnimationIDs()
        {
            _animIDSpeed = Animator.StringToHash("Speed");
            _animIDGrounded = Animator.StringToHash("Grounded");
            _animIDJump = Animator.StringToHash("Jump");
            _animIDFreeFall = Animator.StringToHash("FreeFall");
            _animIDMotionSpeed = Animator.StringToHash("MotionSpeed");
            _animIDShooting = Animator.StringToHash("Shooting");
        }

        private void GroundedCheck()
        {
            Vector3 spherePosition = new Vector3(transform.position.x, transform.position.y - GroundedOffset, transform.position.z);
            Grounded = Physics.CheckSphere(spherePosition, GroundedRadius, GroundLayers, QueryTriggerInteraction.Ignore);
            if (_hasAnimator)
            {
                _animator.SetBool(_animIDGrounded, Grounded);
            }
        }

        private void CameraRotation()
        {
            Vector2 look = _input.GetLook();
            if (look.sqrMagnitude >= _threshold && !LockCameraPosition)
            {
                float deltaTimeMultiplier = IsCurrentDeviceMouse ? 1.0f : Time.deltaTime;
                _cinemachineTargetYaw += look.x * deltaTimeMultiplier;
                _cinemachineTargetPitch += look.y * deltaTimeMultiplier;
            }
            _cinemachineTargetYaw = ClampAngle(_cinemachineTargetYaw, float.MinValue, float.MaxValue);
            _cinemachineTargetPitch = ClampAngle(_cinemachineTargetPitch, BottomClamp, TopClamp);
            CinemachineCameraTarget.transform.rotation = Quaternion.Euler(_cinemachineTargetPitch + CameraAngleOverride, _cinemachineTargetYaw, 0.0f);
        }

        private void Move()
        {
            Vector2 moveInput = _input.GetMove();
            float targetSpeed = _input.IsSprinting() ? SprintSpeed : MoveSpeed;
            if (moveInput == Vector2.zero)
                targetSpeed = 0.0f;

            float currentHorizontalSpeed = new Vector3(_controller.velocity.x, 0f, _controller.velocity.z).magnitude;
            float speedOffset = 0.1f;
            float inputMagnitude = _input.IsAnalog() ? moveInput.magnitude : 1f;

            if (currentHorizontalSpeed < targetSpeed - speedOffset || currentHorizontalSpeed > targetSpeed + speedOffset)
            {
                _speed = Mathf.Lerp(currentHorizontalSpeed, targetSpeed * inputMagnitude, Time.deltaTime * SpeedChangeRate);
                _speed = Mathf.Round(_speed * 1000f) / 1000f;
            }
            else
            {
                _speed = targetSpeed;
            }

            _animationBlend = Mathf.Lerp(_animationBlend, targetSpeed, Time.deltaTime * SpeedChangeRate);
            if (_animationBlend < 0.01f)
                _animationBlend = 0f;

            Vector3 inputDirection = new Vector3(moveInput.x, 0f, moveInput.y).normalized;

            if (!isShooting && moveInput != Vector2.zero)
            {
                _targetRotation = Mathf.Atan2(inputDirection.x, inputDirection.z) * Mathf.Rad2Deg + _mainCamera.transform.eulerAngles.y;
                float rotation = Mathf.SmoothDampAngle(transform.eulerAngles.y, _targetRotation, ref _rotationVelocity, RotationSmoothTime);
                transform.rotation = Quaternion.Euler(0f, rotation, 0f);
            }

            Vector3 moveDirection = isShooting ?
                (transform.forward * moveInput.y + transform.right * moveInput.x) :
                Quaternion.Euler(0f, _targetRotation, 0f) * Vector3.forward;

            _controller.Move(moveDirection.normalized * (_speed * Time.deltaTime) + new Vector3(0f, _verticalVelocity, 0f) * Time.deltaTime);

            if (_hasAnimator)
            {
                _animator.SetFloat(_animIDSpeed, _animationBlend);
                _animator.SetFloat(_animIDMotionSpeed, inputMagnitude);
            }

            if (isShooting)
            {
                Vector2 screenCentre = new Vector2(Screen.width / 2, Screen.height / 2);
                Ray ray = _mainCamera.ScreenPointToRay(screenCentre);
                Vector3 hitPoint = Physics.Raycast(ray, out RaycastHit hit) ? hit.point : ray.GetPoint(70);
                Vector3 aimDirection = (hitPoint - transform.position).normalized;
                aimDirection.y = 0;
                Quaternion targetRot = Quaternion.LookRotation(aimDirection);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * 10f);
            }
        }

        private void JumpAndGravity()
        {
            if (Grounded)
            {
                _fallTimeoutDelta = FallTimeout;
                if (_hasAnimator)
                {
                    _animator.SetBool(_animIDJump, false);
                    _animator.SetBool(_animIDFreeFall, false);
                }
                if (_verticalVelocity < 0f)
                {
                    _verticalVelocity = -2f;
                }
                if (_input.IsJumping() && _jumpTimeoutDelta <= 0f)
                {
                    _verticalVelocity = Mathf.Sqrt(JumpHeight * -2f * Gravity);
                    if (_hasAnimator)
                    {
                        _animator.SetBool(_animIDJump, true);
                    }
                }
                if (_jumpTimeoutDelta >= 0f)
                {
                    _jumpTimeoutDelta -= Time.deltaTime;
                }
            }
            else
            {
                _jumpTimeoutDelta = JumpTimeout;
                if (_fallTimeoutDelta >= 0f)
                {
                    _fallTimeoutDelta -= Time.deltaTime;
                }
                else if (_hasAnimator)
                {
                    _animator.SetBool(_animIDFreeFall, true);
                }
            }
            if (_verticalVelocity < _terminalVelocity)
            {
                _verticalVelocity += Gravity * Time.deltaTime;
            }
        }

        #endregion

        #region Shooting Methods

        private void StartShooting()
        {
            if (!isShooting)
            {
                isShooting = true;
                // Trigger the ink particle effect when shooting starts.
                if (inkParticle != null)
                    inkParticle.Play();
                shootingCoroutine = StartCoroutine(ShootContinuously());
            }
        }

        private void StopShooting()
        {
            isShooting = false;
            if (shootingCoroutine != null)
            {
                StopCoroutine(shootingCoroutine);
                if (_hasAnimator)
                {
                    _animator.SetBool(_animIDShooting, false);
                }
            }
            // Stop the ink particle effect when shooting stops.
            if (inkParticle != null)
                inkParticle.Stop();
        }

        private IEnumerator ShootContinuously()
        {
            while (isShooting)
            {
                Fire();
                yield return new WaitForSeconds(fireRate);
            }
        }

        private void Fire()
        {
            if (_hasAnimator)
            {
                _animator.SetBool(_animIDShooting, true);
                // You can add your bullet firing logic here.
            }
        }

        private void ShootFromCenter()
        {
            Vector2 screenCentre = new Vector2(Screen.width / 2, Screen.height / 2);
            Ray ray = _mainCamera.ScreenPointToRay(screenCentre);
            Vector3 hitPoint = Physics.Raycast(ray, out RaycastHit hit) ? hit.point : ray.GetPoint(70);
            Vector3 direction = (hitPoint - gunPoint.position).normalized;

            GameObject bullet = Instantiate(bulletPrefab, gunPoint.position, Quaternion.identity);
            bullet.transform.forward = direction;
            bullet.GetComponent<Rigidbody>().velocity = direction * 100;
        }

        #endregion

        #region Shooting Visual Polish Methods

        /// <summary>
        /// This method merges the visual polish from the separate shooting system.
        /// </summary>
        private void VisualPolish()
        {
            if (parentController != null && !DOTween.IsTweening(parentController))
            {
                parentController.DOComplete();
                Vector3 localPos = parentController.localPosition;
                parentController.DOLocalMove(localPos - new Vector3(0, 0, 0.2f), 0.03f)
                    .OnComplete(() => parentController.DOLocalMove(localPos, 0.1f).SetEase(Ease.OutSine));
                impulseSource?.GenerateImpulse();
            }
            if (splatGunNozzle != null && !DOTween.IsTweening(splatGunNozzle))
            {
                splatGunNozzle.DOComplete();
                splatGunNozzle.DOPunchScale(new Vector3(0, 1, 1) / 1.5f, 0.15f, 10, 1);
            }
        }

        // Remaps the freeLookCamera's Y-axis value to an angle range.
        private float RemapCamera(float value, float from1, float to1, float from2, float to2)
        {
            return (value - from1) / (to1 - from1) * (to2 - from2) + from2;
        }

        // Helper method to rotate the parent controller to face the camera's forward direction.
        private void RotateParentToCamera()
        {
            if (parentController != null && _mainCamera != null)
            {
                Vector3 forward = _mainCamera.transform.forward;
                forward.y = 0;
                forward.Normalize();
                Quaternion targetRotation = Quaternion.LookRotation(forward);
                parentController.rotation = Quaternion.Slerp(parentController.rotation, targetRotation, desiredRotationSpeed);
            }
        }

        #endregion

        #region Utility Methods

        private static float ClampAngle(float lfAngle, float lfMin, float lfMax)
        {
            if (lfAngle < -360f) lfAngle += 360f;
            if (lfAngle > 360f) lfAngle -= 360f;
            return Mathf.Clamp(lfAngle, lfMin, lfMax);
        }

        private void OnDrawGizmosSelected()
        {
            Color transparentGreen = new Color(0f, 1f, 0f, 0.35f);
            Color transparentRed = new Color(1f, 0f, 0f, 0.35f);
            Gizmos.color = Grounded ? transparentGreen : transparentRed;
            Gizmos.DrawSphere(new Vector3(transform.position.x, transform.position.y - GroundedOffset, transform.position.z), GroundedRadius);
        }

        private void OnFootstep(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f && FootstepAudioClips.Length > 0)
            {
                int index = Random.Range(0, FootstepAudioClips.Length);
                AudioSource.PlayClipAtPoint(FootstepAudioClips[index], transform.TransformPoint(_controller.center), FootstepAudioVolume);
            }
        }

        private void OnLand(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f)
            {
                AudioSource.PlayClipAtPoint(LandingAudioClip, transform.TransformPoint(_controller.center), FootstepAudioVolume);
            }
        }

        #endregion
    }
}
