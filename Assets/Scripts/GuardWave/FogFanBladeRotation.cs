using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// Rotates the authored fan blades around their local hub while preserving
    /// their prefab positions and orientations as the starting pose.
    /// </summary>
    [RequireComponent(typeof(FogBuildingWind))]
    public sealed class FogFanBladeRotation : MonoBehaviour
    {
        [Header("Blade rotation")]
        [SerializeField] private Transform[] _blades;
        [SerializeField] private Vector3 _localHubPosition = new Vector3(0f, 2.05f, 0.205f);
        [SerializeField] private Vector3 _localRotationAxis = Vector3.forward;
        [SerializeField] private float _rotationSpeedDegreesPerSecond = 900f;

        private FogBuildingWind _wind;
        private Vector3[] _initialLocalPositions;
        private Quaternion[] _initialLocalRotations;
        private float _rotationAngle;

        private void Awake()
        {
            _wind = GetComponent<FogBuildingWind>();
            _initialLocalPositions = new Vector3[_blades.Length];
            _initialLocalRotations = new Quaternion[_blades.Length];
            for (int i = 0; i < _blades.Length; i++)
            {
                _initialLocalPositions[i] = _blades[i].localPosition;
                _initialLocalRotations[i] = _blades[i].localRotation;
            }
        }

        private void Update()
        {
            if (!_wind.IsOperational) return;

            _rotationAngle = Mathf.Repeat(
                _rotationAngle + _rotationSpeedDegreesPerSecond * Time.deltaTime, 360f);
            Quaternion rotation = Quaternion.AngleAxis(_rotationAngle, _localRotationAxis.normalized);
            for (int i = 0; i < _blades.Length; i++)
            {
                _blades[i].localPosition = _localHubPosition +
                    rotation * (_initialLocalPositions[i] - _localHubPosition);
                _blades[i].localRotation = rotation * _initialLocalRotations[i];
            }
        }
    }
}
