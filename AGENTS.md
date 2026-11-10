# AGENTS.md

## Managers 호출 패턴

- 전역 시스템은 `Managers`의 정적 접근자(`Managers.Resource`, `Managers.Input` 등)를 통해 호출한다. 매니저 접근에 방어적인 null 검사를 추가하지 않는다.
- `Managers.Instance`에 처음 접근하면 `Managers.Init()`이 실행되어 영속적인 `@Manager`를 찾거나 생성하고 매니저를 초기화한다.
- 개별 매니저는 기본적으로 일반 C# 클래스로 유지하고, 씬별 설정은 씬 컨텍스트에 둔다.
- 매니저를 추가할 때 공통 기능은 `Assets/Scripts/Managers/Core/`, 게임 전용 기능은 `Assets/Scripts/Managers/Contents/`에 두고 `Managers.cs`에 필드와 정적 접근자를 등록한다. 필요한 생명주기 메서드만 호출한다.

## 작업 범위와 확인

- 정확성·안전·권한 확인을 지키면서 필요한 파일과 구간만 확인한다. 관련 오류나 의존성의 근거가 있을 때만 조사 범위를 넓히고 무관한 반복 검증을 피한다.
- 요청과 직접 관련된 코드만 수정한다. 요청받지 않은 이름 변경, 리팩터링, 기존 동작 변경을 하지 않는다. 테스트 코드는 작성하지 않는다.
- Unity 생명주기와 Inspector 설정을 고려해 불필요한 null 검사를 추가하지 않는다.

## 주석

- Unity `MonoBehaviour`의 Lifecycle 및 Callback 메서드를 제외한 모든 메서드 위에 XML `summary`를 작성한다. 동작, 주요 입력값, 반환값 또는 변경되는 주요 상태를 설명한다.
- 메서드 내부 주석은 2~3문장 이내의 짧은 평문으로 쓰고 이모지나 Markdown 문법을 사용하지 않는다.

## 필드와 접근 제한자

- Inspector에 노출할 값만 `[SerializeField] private`으로 선언한다. `[SerializeField] public`은 사용하지 않고, 직렬화가 불필요한 필드는 `private`으로 선언한다. 외부에서 참조해야 하는 상태만 `public` 프로퍼티로 공개한다.
- 필드는 `const`, `static`, 역할별 `[Header("")]` 그룹 순으로 배치한다. 각 Header 그룹 안에서는 `[SerializeField] private`, `private/protected`, `public` 순으로 둔다.
- 모든 필드와 메서드에 접근 제한자를 명시한다. Unity Lifecycle/Callback 메서드는 생략할 수 있지만 `virtual`, `override`, `abstract` 등을 사용하면 명시한다.

## using과 이름

- `using`은 `System`, `UnityEngine`, Unity 패키지, 외부 패키지, 프로젝트 namespace 순으로 작성하고 그룹 사이에 빈 줄을 둔다. 그룹 안에서는 알파벳순으로 정렬하고 미사용 `using`은 제거한다.
- private 필드: `_camelCase`; public 프로퍼티·메서드·enum 타입·enum 값: `PascalCase`; 지역 변수: `camelCase`; 상수: `SCREAMING_SNAKE_CASE`.

## Git

- 커밋 메시지는 제안만 가능하다. Git CLI는 실행하지 않는다.
