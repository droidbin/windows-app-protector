# Windows App Protector

WinUI 3 기반 Windows 앱 실행 차단 도구입니다. 보호 목록에 등록한 `.exe` 파일을 Windows IFEO(Image File Execution Options) 규칙으로 차단합니다.

현재 배포 버전: `1.1.6`

## 배포 파일

최종 배포 파일은 아래 하나입니다.

```text
dist\installer\Setup.exe
```

사용자는 `Setup.exe`만 실행하면 됩니다. 설치 프로그램은 관리자 권한을 요청한 뒤 다음 작업을 자동으로 처리합니다.

- 실행 중인 기존 Windows App Protector 프로세스 종료
- 이전 버전에서 남긴 관리형 IFEO 차단 규칙 정리
- 구버전 설정 폴더 `%APPDATA%\WindowsAppProtector` 삭제
- `C:\Program Files\Windows App Protector`에 WinUI 앱 설치
- 선택 시 바탕화면 및 시작 메뉴 바로가기 생성
- Windows 앱 제거 목록 등록
- 설치 완료 후 앱 실행

## 빌드

개발 PC에서 아래 명령만 실행하면 WinUI 앱과 설치 파일이 함께 생성됩니다.

```powershell
.\build_setup.bat
```

빌드 결과:

```text
dist\installer\Setup.exe
```

## 업데이트 배포

자동 업데이트는 GitHub Releases의 최신 릴리즈를 기준으로 동작합니다.

- 릴리즈 태그는 `v1.1.6`처럼 앱 버전과 맞춥니다.
- 릴리즈 자산에는 `Setup.exe`를 첨부하는 것을 권장합니다.
- `Setup.exe`가 없으면 `WindowsAppProtector.zip` 안의 `Setup.exe`를 찾아 설치합니다.
- private 저장소를 사용할 경우 `WINDOWS_APP_PROTECTOR_GITHUB_TOKEN` 환경 변수 또는 `%PROGRAMDATA%\Windows App Protector\github-token.txt`에 GitHub 토큰을 넣어야 합니다.

## 주요 기능

- 여러 `.exe` 앱 보호 목록 관리
- 선택 앱 잠금/해제
- 목록 잠금/해제
- 앱 최초 실행 및 백그라운드 복귀 시 PIN 인증
- 일정 시간 유휴 상태 시 보호 목록 자동 잠금
- Windows 시작 시 자동 실행 ON/OFF
- GitHub Releases 기반 자동 업데이트
- 전역 단축키 등록 및 설정
- 중복 단축키 감지
- X 버튼 클릭 시 백그라운드 숨김 아이콘으로 전환
- 프로그램 종료 메뉴 선택 시 백그라운드 프로세스까지 종료
- Windows Service 기반 보호 규칙 감시 및 복구
- 설정 저장 위치: `%APPDATA%\WindowsAppProtector.WinUI`
- 서비스 감시 설정 위치: `%PROGRAMDATA%\Windows App Protector\config.json`
- Windows App SDK 2.0.1 기반 WinUI 3 앱
- 설치형 `Setup.exe` 패키지

## 기본 단축키

- `Ctrl+Alt+W`: 창 보이기/숨기기
- `Ctrl+Shift+L`: 목록 잠금
- `Ctrl+Shift+U`: 목록 해제

## 차단 방식

보호 기능은 아래 레지스트리에 관리형 규칙을 등록해서 동작합니다.

```text
HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options
```

차단된 앱을 새로 실행하면 대상 앱을 강제 종료하지 않고 실행만 막습니다. 이미 실행 중인 앱은 유지되므로 카카오톡처럼 세션 유지가 중요한 앱을 잠갔다 풀 때 재로그인이 발생하지 않도록 설계했습니다.

## 보호 서비스

설치 시 `WindowsAppProtectorService`가 자동 시작 서비스로 등록됩니다.

서비스는 `%PROGRAMDATA%\Windows App Protector\config.json`을 기준으로 보호 상태를 감시하고, 사용자가 UI 프로세스를 작업 관리자에서 강제 종료해도 관리형 IFEO 차단 규칙을 주기적으로 복구합니다.

서비스는 사용자의 데스크톱 UI를 직접 다시 띄우지는 않습니다. UI가 종료되어도 차단 규칙은 유지/복구되며, 설정 변경이나 단축키 기능이 필요하면 사용자가 앱을 다시 실행하면 됩니다.

## 제거

Windows 설정의 앱 제거 목록에서 `Windows App Protector`를 제거하거나 설치 폴더의 `uninstall.bat`을 실행하면 됩니다.

제거 시 Windows Service, 관리형 IFEO 규칙, 서비스 설정, 바로가기가 함께 정리됩니다.
