# LlmRemote

원격에서 휴대폰 브라우저로 윈도우 PC의 특정 창을 선택하여 화면을 보고, 텍스트를 입력할 수 있는 원격 접속 앱입니다.

## 주요 기능

- 윈도우 계정으로 로그인 (보안)
- 실행 중인 창 목록에서 원하는 창 선택
- 선택한 창의 화면을 실시간 스트리밍 (WebP, 변화 영역만 전송)
- 텍스트 입력 (한글/영어) 및 엔터 전송
- 터치 스크롤, 탭 클릭
- 핀치 줌으로 창 크기 조절
- HTTPS 암호화 통신

## 실행 방법

### 1. 사전 준비

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 설치

### 2. 설정

`config.example.json`을 복사하여 `config.json`을 만듭니다.

```json
{
  "port": 8080,
  "certPath": "",
  "certPassword": ""
}
```

| 항목 | 설명 |
|------|------|
| `port` | 서버가 사용할 포트 번호 |
| `certPath` | SSL 인증서 경로 (.pfx). 비워두면 자체 서명 인증서 자동 생성 |
| `certPassword` | 인증서 비밀번호. 자체 서명 시 비워두면 됨 |

### 3. 빌드 및 실행

```bash
dotnet build
dotnet run
```

### 4. 접속

브라우저에서 `https://<PC IP>:<포트>` 로 접속합니다.

- 자체 서명 인증서 사용 시 "안전하지 않음" 경고가 뜹니다. "계속 진행"을 누르면 됩니다.
- 공인 인증서(.pfx) 설정 시 경고 없이 접속됩니다.

윈도우 계정 아이디/비밀번호로 로그인하면 창 목록이 표시됩니다.

## 공유기 포트 포워딩 설정

외부에서 접속하려면 공유기에서 포트 포워딩을 설정해야 합니다.

### 설정 예시

| 항목 | 값 |
|------|-----|
| 외부 포트 | 원하는 포트 (예: 53200) |
| 내부 IP | PC의 내부 IP (예: 192.168.0.10) |
| 내부 포트 | `config.json`의 `port` 값 (예: 8080) |
| 프로토콜 | TCP |

### 설정 순서

1. PC의 내부 IP 확인: `ipconfig` 명령어로 IPv4 주소 확인
2. 공유기 관리 페이지 접속 (보통 192.168.0.1 또는 192.168.1.1)
3. 포트 포워딩 / 가상 서버 메뉴에서 위 내용 입력
4. `config.json`의 `port`를 내부 포트와 맞춤
5. 외부에서 `https://<공인IP 또는 도메인>:<외부포트>` 로 접속

### 공인 인증서 사용 시

도메인이 있다면 Let's Encrypt 등으로 발급받은 `.pfx` 인증서를 `config.json`에 설정하면 브라우저 경고 없이 접속할 수 있습니다.

## 참고: SSL 인증서 생성

### 자체 서명 인증서 (기본)

`config.json`에서 `certPath`를 비워두면 서버가 자동으로 자체 서명 인증서를 생성합니다. 별도 작업이 필요 없지만, 브라우저에서 "안전하지 않음" 경고가 표시됩니다.

### Let's Encrypt 무료 공인 인증서

도메인을 보유하고 있다면 무료로 공인 인증서를 발급받을 수 있습니다.

#### 1. Certbot 설치

Windows에서는 [Certbot for Windows](https://certbot.eff.org/instructions?ws=other&os=windows)를 설치합니다.

#### 2. 인증서 발급

```bash
certbot certonly --standalone -d yourdomain.com
```

발급된 파일은 보통 `C:\Certbot\live\yourdomain.com\` 에 생성됩니다.

#### 3. PFX 변환

Let's Encrypt는 `.pem` 형식으로 발급하므로 `.pfx`로 변환해야 합니다.

```bash
openssl pkcs12 -export -out yourdomain.pfx -inkey privkey.pem -in fullchain.pem -password pass:yourpassword
```

#### 4. config.json 설정

```json
{
  "port": 8080,
  "certPath": "C:\\certs\\yourdomain.pfx",
  "certPassword": "yourpassword"
}
```

#### 참고 사항

- Let's Encrypt 인증서는 90일마다 갱신이 필요합니다 (`certbot renew`)
- 발급 시 도메인의 DNS A 레코드가 해당 PC의 공인 IP를 가리키고 있어야 합니다
- 443 포트가 열려 있어야 발급이 가능합니다 (발급 후에는 다른 포트 사용 가능)
