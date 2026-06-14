# Windows App Protector WinUI 3

WinUI 3 전면 개편용 프로젝트 초안입니다.

현재 작업 PC에는 .NET SDK/MSBuild가 없어 빌드 검증은 하지 못했습니다. Visual Studio 2022, .NET 8 SDK, Windows App SDK가 설치된 PC에서 이어서 빌드하세요.

## 목표 구조

- `Views`: 화면 XAML
- `ViewModels`: 화면 상태와 커맨드
- `Models`: 설정/보호 앱 모델
- `Services`: 실행 차단, 설정 저장, 전역 단축키, 관리자 권한 처리
- `Assets/Fonts`: Noto Sans 계열 폰트 번들 위치

상용 프로그램 수준 UI를 위해 메인 화면은 보호 목록 중심으로 유지하고, 세부 옵션은 Settings 다이얼로그로 분리합니다.
