using UnityEngine;

public class PaintBullet : MonoBehaviour
{
    public GameObject paintDecalPrefab;  // Assign a paint decal prefab (a flat quad with a paint texture)
    public float paintSize = 1f;         // Control the size of the paint splat
    public float destroyTime = 5f;       // Time before the bullet disappears

    private void Start()
    {
        Destroy(gameObject, destroyTime);
    }

    private void OnCollisionEnter(Collision collision)
    {
        // Get the hit point and normal
        ContactPoint contact = collision.contacts[0];
        Vector3 hitPoint = contact.point;
        Quaternion hitRotation = Quaternion.LookRotation(-contact.normal);

        // Instantiate the paint splat at the collision point
        GameObject paint = Instantiate(paintDecalPrefab, hitPoint + (contact.normal * 0.05f), hitRotation);
        paint.transform.localScale *= paintSize;

        // Destroy bullet after hitting something
        Destroy(gameObject);
    }
}
