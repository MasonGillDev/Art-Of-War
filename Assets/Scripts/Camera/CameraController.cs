using UnityEngine;

public class CameraController : MonoBehaviour
{
    [Header("Panning Settings")]
    public float panSpeed = 20f;      // Speed at which the camera moves horizontally and vertically
    public float panBorderThickness = 10f; // Optional: thickness in pixels for edge scrolling (if desired)

    [Header("Zooming Settings")]
    public float zoomSpeed = 2f;      // Speed at which the camera zooms in/out
    public float minOrthoSize = 2f;   // Minimum orthographic size (zoomed in)
    public float maxOrthoSize = 50f;  // Maximum orthographic size (zoomed out)

    private Camera cam;

    void Start()
    {
        cam = GetComponent<Camera>();
        if (cam == null)
        {
            Debug.LogWarning("CameraController: No Camera component found on this GameObject.");
        }
    }

    void Update()
    {
        PanCamera();
        ZoomCamera();
    }

    void PanCamera()
    {
        // Get keyboard input for horizontal and vertical movement
        float moveX = Input.GetAxis("Horizontal"); // A/D or Left/Right arrow keys
        float moveY = Input.GetAxis("Vertical");   // W/S or Up/Down arrow keys

        // Move the camera based on input
        Vector3 panMovement = new Vector3(moveX, moveY, 0f) * panSpeed * Time.deltaTime;
        transform.position += panMovement;

        // OPTIONAL: Add edge scrolling if you want the camera to move when the mouse is near the screen edge.
        if (Input.mousePosition.x >= Screen.width - panBorderThickness)
        {
            transform.position += Vector3.right * panSpeed * Time.deltaTime;
        }
        if (Input.mousePosition.x <= panBorderThickness)
        {
            transform.position += Vector3.left * panSpeed * Time.deltaTime;
        }
        if (Input.mousePosition.y >= Screen.height - panBorderThickness)
        {
            transform.position += Vector3.up * panSpeed * Time.deltaTime;
        }
        if (Input.mousePosition.y <= panBorderThickness)
        {
            transform.position += Vector3.down * panSpeed * Time.deltaTime;
        }
    }

    void ZoomCamera()
    {
        // Only if the camera is orthographic (typical for 2D)
        if (cam && cam.orthographic)
        {
            // Use the mouse scroll wheel for zooming
            float scrollAmount = Input.GetAxis("Mouse ScrollWheel");
            cam.orthographicSize -= scrollAmount * zoomSpeed;
            
            // Clamp the orthographic size so that the camera doesn't zoom in too far or too far out
            cam.orthographicSize = Mathf.Clamp(cam.orthographicSize, minOrthoSize, maxOrthoSize);
        }
    }
}
