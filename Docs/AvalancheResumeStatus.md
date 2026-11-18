# Avalanche 눈사태 전환 재개 현황

- 기반 브랜치: `feature/avalanche-work-pending` (`dd5f43231be6`)
- 작업 브랜치: `feature/avalanche-resume`
- 목적: 기존 중단 작업을 보존하고 눈사태 스테이지의 검증 가능한 결함을 우선 보완한다.
- 검증 수준: GitHub 소스·씬 YAML·설정 파일 정적 확인. 이 작업 환경에는 Unity Editor가 없어 Play Mode, 빌드, Profiler 검증은 수행하지 않았다.
- 인수 기준과 기획 수치는 [AvalancheStageDesign.md](AvalancheStageDesign.md)를 따른다.

## 코드에서 확인한 구현

- `AvalancheStage`: 준비→5초 경보→0초 및 12초 눈 방출→20초 이후 관찰→성공·실패·재도전 흐름.
- `AvalancheSnow`: 60×100 격자, 이동층과 정지층, 질량 유량·퇴적, 방벽 경계 투영, 눈 표면 메시, 부피 오차·수치 이상 검사.
- `AvalancheConstruction`: 세 시민 구역·6명 배치, 방벽/받침/접합 재고, 접합·Undo·실패 복원.
- `AvalancheCitizen`: 5개 지점 샘플, 충돌과 매몰 노출 시간 추적.
- `AvalancheHud`와 `OutGameStageSelect`: 공통 UI 어댑터 및 네 번째 스테이지 선택 카드.
- 씬에 `AvalancheStage`, `AvalancheConstruction`, `AvalancheHud`, `StageCameraController`의 스크립트 GUID와 주요 Inspector 참조가 존재하고, Avalanche 씬이 빌드 설정에 등록되어 있음을 확인했다.

## 이번 재개 작업의 수정

1. `StageCameraController`: Windows 휠 델타를 논리 단위로 정규화하고, 공통 카메라 조작에서 회전 각도가 다음 프레임에 원상복구되지 않도록 내부 yaw 값을 갱신한다.
2. `AvalancheConstruction`: 접합 블록이 X/Z 배치 범위 밖이나 발원부(Z≥48m)에 놓이지 않도록 막고, 초기 힌트를 눈사태 기준으로 갱신한다.
3. `Avalanche.unity`: `AvalanchePiece.PhysicsSettings`의 기존 `_waterDensity`/ `_flowDrag` 값을 현재 필드 `_materialDensity` 기준으로 정리한다.
4. `AvalancheSnow`: 비활성 방벽이 통과 경계 캐시에 계속 남는 상황을 방지하고 정지층의 비정상 수치도 기술 오류로 보고한다.

## 아직 해결되지 않은 구현 차이와 제약

- **공간 점유 변화 처리**: 명세는 자재 이동 시 눈이 차지할 수 없게 된 셀의 이동층·정지층을 인접 셀로 옮기거나 임시 보관할 것을 요구한다. 현재 `AvalancheSnow`는 콜라이더를 셀 **경계**의 통과 높이로 투영하지만, 셀 **내부** 점유로 밀려나는 눈을 재배분하는 별도 보관 시스템은 없다.
- **충돌/퇴적의 실제 결과**: 근사 유량·마찰·하중식이 구현되어 있으나 첫 방출이 마을까지 도달하는지, 방벽과의 충돌 후 부피 및 조인트 파손이 안정적인지는 실행 검증이 필요하다.
- **작업 브랜치의 공통 변경**: HUD 컨텍스트, 카메라, 배치/조인트 추출, 낙하 점선 UI 제거 등 다른 스테이지에도 영향이 있는 변경이 포함되어 있다. Flood·Fog·Lava의 회귀 검증 전에는 `master`에 병합하지 않는다.
- **문서/에셋의 이전 명칭**: 일부 `Water`, `QuietWater` 이름은 기존 자산 호환을 위해 유지하고 있다. 동작 검증 이전에는 불필요한 GUID·에셋 이름 변경을 하지 않는다.

## Unity Editor 수동 인수 확인 순서

1. Unity `6000.3.28f1`에서 `feature/avalanche-resume`을 열고 Console 컴파일 오류 및 Missing Script를 확인한다.
2. Game 씬에서 네 번째 `WHITE DESCENT`를 선택하여 Avalanche에 진입하고, 초기 규칙 팝업을 닫은 뒤 마우스 휠 줌·우클릭 드래그·휠 회전·Home 전체 보기를 확인한다.
3. 시민 6명을 세 구역에 최소 1명씩 배치한다. 미배치/구역 미달에는 준비 완료가 잠기고, 6명 충족 시 활성화되는지 확인한다.
4. 자재·접합 블록을 설치하고 Z≥48m, 배치 영역 밖의 설치가 거절되는지 확인한다. 우클릭 회수·Ctrl+Z·재고·조인트 복원도 확인한다.
5. Ready 후 5초 경보, 0초/12초 방출, 20초 이후 안전 판정, 60초 제한, 실패/성공 팝업과 재도전을 확인한다.
6. 방벽 없는 6명 배치에서 실제 눈이 마을에 도달하여 실패하는지 확인하고, 동일 배치 3회 재현의 도착 위치 오차 ≤0.6m 및 시간 오차 ≤0.2초를 측정한다.
7. 정면·사선 방벽·접합 설치로 우회/퇴적/파손/두 번째 방출의 잔류 효과를 비교한다. 부피 오차 1% 이내와 유효한 성공 배치를 확인한다.
8. Flood·Fog·Lava에서 카드, 마우스 조작, 준비, 실패·성공, 카메라가 기존과 동일하게 동작하는지 확인한다.
9. 최대 재고+두 번째 방출이 포함된 20초 구간을 Windows 빌드 Profiler로 측정한다. 테스트한 실제 PC 사양과 프레임 시간 결과를 별도로 기록한다.

**현재 상태**: 위 1~9번은 아직 실제 실행 결과가 아니다. 확인 결과가 확보되기 전까지 눈사태 완성, 빌드 정상, 목표 성능 달성을 주장하지 않는다.
