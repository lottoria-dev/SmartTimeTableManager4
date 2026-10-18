# Smart Timetable Manager 4

교사가 큰 시간표에서 수업을 조회하고, 클릭·드래그로 교체·보강을 처리하는 Windows 프로그램입니다. 한국 학교의 장판지식 수업 교체 업무에 집중합니다.

**버전 4.0.4** · 개발: **MathTime** · [공식 안내](https://mathtime.kr/?page=timetable)

본 프로그램은 개발자가 기능을 기획하고, 일부 코드 작성·검토 및 문서화 과정에서 **생성형 AI를 활용했습니다.**

## 주요 기능

- 조회 / 맞교환 / 보강 / 수동 교체 / AI 자동의 다섯 모드.
- 지정 시간 이동, 수동 연쇄 교체, 교사별 이동·재변경·연강 허용 확인.
- 교장·교감 특별 보강, 잠금, 날짜별 학년 행사 제외.
- 기초 요일 일과 적용, 월 관리·결보강 통계·변경 다이어그램.
- 실행 취소·다시 실행, 작업 저장·복구, Excel·CSV 내보내기.

자동 교체는 PC 안에서 수행합니다. 외부 AI API에 시간표를 전송하지 않습니다.

## 사용

1. [4.0.4 Release](https://github.com/lottoria-dev/SmartTimeTableManager4/releases/tag/4.0.4)의 실행 ZIP 압축 해제 → `TimeTableManager.exe` 실행.
2. `templates`의 동일셀·분리셀 Excel 중 하나를 학교 자료로 편집.
3. 파일 → 시간표 파일 열기 → 모드 선택 → 변경 적용.
4. 작업은 `.ttm`으로 저장. 도움말에서 학교 상황별 활용 확인.

두 Excel 양식 모두 지원합니다. 동일셀은 교과명과 교사명을 줄바꿈으로 입력하며, 분리셀은 교과 행 아래에 교사 행을 둡니다. 예제 이름은 모두 가상 이름입니다. CSV와 [편집 안내](templates/템플릿_사용방법.txt)도 제공합니다.

## 소스 빌드

- .NET 10 SDK / Visual Studio의 .NET 데스크톱 개발 환경.
- `TimeTableManager.sln` 열기 → `TimeTableManager.App` 시작 프로젝트 → 빌드·실행.
- `build_release.cmd`: 자체 포함 Windows x64 게시·실행 ZIP·Templates ZIP·공개 소스 ZIP·SHA-256 생성.
- `export_public_source.cmd`: 컴파일 없이 공개 소스 ZIP·해시 생성.
- 같은 버전 재생성은 `-Force`. 결과 폴더: `artifacts/public`.
- `verify.cmd`: 회귀 사례 실행.

공개 소스는 `tools/PublicSourceFiles.json`에 명시된 파일만 제공합니다. 파일을 추가할 때 이 목록을 갱신하고 공개 소스를 다시 생성합니다. Git 추적 허용 목록도 같은 파일 목록으로 갱신됩니다.

## 버전·사용 조건

- [CHANGELOG.md](CHANGELOG.md) / [VERSION_HISTORY.txt](VERSION_HISTORY.txt): 버전별 변경 내역.
- 태그는 `4.0.4`처럼 버전명만 사용합니다.
- 교육기관 교직원의 학급 운영·업무 목적 무료 사용. 상업적 영리 목적 사용 금지.
- [LICENSE.md](LICENSE.md), [LICENSE_ORIGINAL.txt](LICENSE_ORIGINAL.txt), [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 참조.

문의: [mathtime.ai@gmail.com](mailto:mathtime.ai@gmail.com)
