# MangaViewer (WinUI 3)

Windows 데스크톱에서 만화(이미지) 폴더를 읽고, 두 페이지/한 페이지 보기, 읽기 방향 전환, 표지 분리, 썸네일 패널, OCR(텍스트 영역 인식/복사), 그리고 e-hentai 검색/스트리밍 열람까지 지원하는 WinUI 3 기반 리더입니다.

[English README](./README.md)

## 주요 기능

- 리더(Reader)
  - 한 페이지/두 페이지 보기 자동 전환
  - 읽기 방향 전환(정방향/역방향)
  - 표지 한 장만 또는 두 장으로 보기 전환
  - 우측 썸네일 패널(핸들로 가변 폭 조절, 상태 유지)
  - 페이지 이동 시 슬라이드 애니메이션 및 이미지 프리페치
  - Mica 배경 타이틀바 및 투명 UI

- OCR(Windows.Media.Ocr)
  - 언어: 자동/일본어/한국어/영어
  - 그룹화: 단어/줄/문단(세로·가로 모드, 간격 휴리스틱)
  - 인식 박스 탭 → 텍스트 클립보드 복사

- 검색(Search) + 스트리밍 열람
  - e-hentai 검색 결과 그리드(무한 스크롤, 썸네일 지연 로드)
  - 타일 확대/축소: Ctrl + 마우스 휠(부드러운 보간)
  - 컨텍스트 메뉴: 상세 보기, 작가/그룹/태그 재검색
  - 스트리밍 다운로드: 메모리 키(`mem:gid:####.ext`)로 순서 보장 추가 → 리더 즉시 전환

- 상세(Details)
  - 제목/썸네일/언어/작가/그룹/패러디/남/여/기타 태그 표시
  - 태그 탭 → 해당 조건으로 검색 이동

- 설정(Settings)
  - OCR 언어·그룹화·쓰기 방향·문단 간격 슬라이더
  - 태그 폰트 크기, 썸네일 디코드 폭 설정
  - 메모리 이미지 캐시 현황/제한(개수·용량) 설정, 전체/개별 갤러리 캐시 정리

## 요구 사항

- OS: Windows 11 권장
  - TargetFramework: `net9.0-windows10.0.26100.0`
  - SupportedOSPlatformVersion: `10.0.22621.0`
- SDK/툴
  - .NET SDK 9.0+
  - Windows 11 SDK 10.0.26100.x
  - Windows App SDK 1.7.x (NuGet)

## 빌드 & 실행 (VS Code Tasks)

- 제공 태스크(업데이트됨):
  - `restore`: 복원
  - `build Debug x64` / `x86` / `ARM64`: `net9.0-windows10.0.26100.0` 대상으로 Debug 빌드

PowerShell에서 수동 실행 예시:

```pwsh
# 복원
dotnet restore .\MangaViewer\MangaViewer.csproj

# 빌드 (x64 Debug)
dotnet build .\MangaViewer\MangaViewer.csproj -c Debug -f net9.0-windows10.0.26100.0 -p:Platform=x64

# 실행 (x64 Debug)
dotnet run --project .\MangaViewer\MangaViewer.csproj -c Debug -p:Platform=x64
```

## 사용 방법

- 리더: 폴더 열기 → 썸네일/페이지 이동, OCR 실행 후 박스 탭으로 텍스트 복사
- 검색: 키워드 검색 → 항목 클릭 시 스트리밍 열람(메모리 캐시, 디스크 저장 없음)
- 설정: OCR/썸네일/캐시/태그 표시 옵션 조정

## 주의 사항

- 스트리밍 이미지는 디스크에 저장하지 않고 앱 메모리에만 보관됩니다.
- Windows OCR 언어 팩이 설치되어야 해당 언어 인식 품질이 좋습니다.
- 대형 갤러리를 여러 개 열면 메모리 사용량이 증가할 수 있습니다.

## 라이선스

`LICENSE.txt`를 참조하세요.
