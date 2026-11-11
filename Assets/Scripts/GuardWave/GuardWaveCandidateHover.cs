using UnityEngine;
using UnityEngine.EventSystems;

namespace GuardWave
{
    /// <summary>원본 후보 프리팹 버튼의 마우스 호버를 3D UI 툴팁에 전달한다.</summary>
    public sealed class GuardWaveCandidateHover : MonoBehaviour, IPointerEnterHandler, IPointerMoveHandler, IPointerExitHandler
    {
        private GuardWaveHud _hud;
        private int _index;

        /// <summary>호버 대상이 가리키는 카드 인덱스를 초기화한다.</summary>
        public void Initialize(GuardWaveHud hud, int index)
        {
            _hud = hud;
            _index = index;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hud.ShowTooltip(_index, eventData.position);
        }

        public void OnPointerMove(PointerEventData eventData)
        {
            _hud.MoveTooltip(eventData.position);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hud.HideTooltip();
        }
    }
}
