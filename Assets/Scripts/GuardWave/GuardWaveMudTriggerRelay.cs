using UnityEngine;

namespace GuardWave
{
    /// <summary>Forwards child capsule trigger events to the owning mud patch.</summary>
    [DisallowMultipleComponent]
    public sealed class GuardWaveMudTriggerRelay : MonoBehaviour
    {
        private GuardWaveMudPatch _owner;

        public void Initialize(GuardWaveMudPatch owner) => _owner = owner;

        private void OnTriggerEnter(Collider other) => _owner?.ProcessTrigger(other);
        private void OnTriggerStay(Collider other) => _owner?.ProcessTrigger(other);
    }
}
