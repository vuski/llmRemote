# Remote Window Control App — Design Spec

## 목적

로컬 윈도우 PC에서 서버를 실행하면, 원격(휴대폰 브라우저)에서 IP:포트로 접속하여 윈도우에 떠 있는 특정 창을 선택하고, 화면을 보면서 키보드/마우스 입력을 할 수 있는 앱.

## 핵심 요구사항

- 언어: C# (.NET 8)
- 단일 프로젝트: 서버가 웹페이지도 제공하고 WebSocket도 처리
- 윈도우 계정 인증: 아무나 접속 못하게 Windows 로그인으로 보호
- 창 목록: 독립된 최상위 창 목록을 보여주고, 하나를 선택
- 화면 스트리밍: 선택한 창을 캡처 → JPEG 압축 → WebSocket 전송
- 입력 전달: 키보드/마우스 이벤트를 WebSocket으로 받아 대상 창에 전달
- 핀치 줌: 휴대폰에서 핀치 → 서버에서 실제 창 크기 변경 → 리플로우된 화면 전송
- 포트 설정: `config.json`에서 포트 지정

## 아키텍처

```
[휴대폰 브라우저] ←HTTP/WebSocket→ [C# 서버 (ASP.NET Minimal API)]
                                        │
                                   Win32 API
                                   (user32.dll, gdi32.dll)
                                        │
                                   대상 윈도우 창
```

## 인증 흐름

1. 브라우저에서 `http://IP:PORT` 접속
2. 로그인 폼 (윈도우 계정 아이디/비밀번호)
3. 서버: `LogonUser` Win32 API로 검증
4. 성공 시 랜덤 세션 토큰 생성 → 쿠키로 반환
5. 이후 모든 요청/WebSocket 연결에 토큰 검증

## 창 목록 API

- `GET /api/windows` → 보이는 최상위 창 목록 (hwnd, title)
- `EnumWindows` + `IsWindowVisible` + `GetWindowText`로 필터링
- 제목 없는 창, 최소화된 시스템 창 등 제외

## 화면 스트리밍

- WebSocket 연결: `/ws/stream?hwnd={hwnd}`
- 캡처 루프:
  1. `PrintWindow` 또는 `BitBlt`로 창 캡처
  2. JPEG 압축 (품질 조절 가능)
  3. 바이너리 WebSocket 메시지로 전송
  4. 5~10fps 목표, 화면 변화 없으면 전송 스킵

## 입력 전달

- WebSocket 메시지로 입력 이벤트 수신 (JSON)
- 키보드: `PostMessage(WM_KEYDOWN/WM_KEYUP)` 또는 `SendInput`
- 마우스: 클릭 좌표를 창 좌표로 변환 후 `PostMessage(WM_LBUTTONDOWN/UP)`
- 스크롤: `WM_MOUSEWHEEL`

## 핀치 줌 → 창 리사이즈

- 브라우저에서 핀치 제스처 감지 → 비율 계산
- WebSocket으로 새 크기 전송
- 서버: `SetWindowPos`로 창 리사이즈
- 다음 캡처 프레임에 자동 반영

## 설정 파일

```json
{
  "port": 8080
}
```

## 프로젝트 구조

```
llmRemote/
├── config.json
├── LlmRemote.csproj
├── Program.cs                # 진입점, 웹서버 설정
├── Services/
│   ├── AuthService.cs        # LogonUser 기반 윈도우 계정 인증 + 세션 관리
│   ├── WindowService.cs      # 창 열거, 캡처, 리사이즈
│   └── InputService.cs       # 키보드/마우스 입력을 대상 창에 전달
├── Handlers/
│   ├── AuthHandler.cs        # POST /api/login, 로그아웃
│   ├── WindowListHandler.cs  # GET /api/windows
│   └── StreamHandler.cs      # WebSocket /ws/stream — 화면 전송 + 입력 수신
└── wwwroot/
    ├── index.html            # 로그인 → 창 목록 → 뷰어 (SPA)
    ├── app.js                # WebSocket, 핀치 줌, 키/마우스 이벤트
    └── style.css             # 모바일 최적화 스타일
```

## 비고

- 개인용이므로 HTTPS/보안 강화는 초기 버전에서 생략
- 한 번에 하나의 창만 제어
- 한 명의 사용자만 접속하는 것을 가정
