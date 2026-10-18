# Smart Timetable Manager 4

한국 학교의 수업 교체·보강 전용 Windows 프로그램입니다. 큰 시간표에서 클릭·드래그로 수업을 옮기거나 자동 교체안을 검토합니다.

**4.1.0 · 정식 버전** · 개발: **MathTime** · [공식 안내](https://mathtime.kr/timetable.html)

**본 프로그램은 일부 코드 작성·검토 및 문서화에 생성형 AI를 활용했습니다.** 자동 교체는 PC 안에서 수행하며 외부 AI API에 시간표를 전송하지 않습니다.

## 작업 기간

- 파일을 열면 오늘을 기준으로 시작합니다. 기본값은 **1주 후 금요일까지**입니다.
- 작업 기간에서 **이번 주 / 1주 후 / 2주 후 / 3주 후 금요일까지**를 선택합니다. 실제 종료 날짜도 표시합니다.
- 선택한 기간이 다음 달로 이어지면 같은 작업 파일에 날짜를 자동으로 확장합니다.
- **이번 달 전체 조회**와 **다른 월 전체 조회**는 유지합니다. 조회 월을 바꾸어도 작업 기간은 바뀌지 않습니다.
- 오늘 이전과 선택한 기간 밖의 수업은 조회만 가능합니다. 월별 통계 기능은 제거했습니다.
- 기간을 줄여도 기존 배정·잠금·행사 제외·변경 이력을 삭제하지 않습니다.

## 주요 기능

- 조회 / 맞교환 / 보강 / 수동 교체 / AI 자동.
- 지정 시간 이동, 수동 연쇄 교체, 교사별 이동·재변경·연강 허용 확인.
- 교장·교감 특별 보강, 잠금, 날짜별 학년 행사 제외.
- 기초 요일 일과 적용, 변경 다이어그램, 실행 취소·다시 실행.
- 작업 저장·복구, Excel·CSV 배정 결과 내보내기.

AI 자동으로 먼저 배치하고, 수동 교체·맞교환으로 필요한 수업만 조정한 뒤 조회로 최종 확인하세요.

## 처음 사용

1. Visual Studio에서 빌드·실행하거나 실행 ZIP을 새 폴더에 압축 해제합니다.
2. `templates`의 동일셀·분리셀 Excel 중 하나를 학교 자료로 편집합니다.
3. 파일 → 시간표 파일 열기 → 작업 기간 선택 → 모드 선택 → 변경 적용.
4. 작업은 `.ttm`으로 저장합니다. 파일을 다시 열면 선택한 주 수는 유지하고 시작 날짜를 오늘로 갱신합니다.

동일셀은 교과명과 교사명을 줄바꿈으로 입력하며, 분리셀은 교과 행 아래에 교사 행을 둡니다. 예제 이름은 모두 가상 이름입니다. CSV와 [편집 안내](templates/템플릿_사용방법.txt)도 제공합니다.

기존 4.0.4 작업 파일은 불러올 수 있습니다. 4.1.0에서 저장한 새 형식은 4.0.4에서 열 수 없으므로 기존 파일도 보관하세요. 저장 시 파일명에 날짜·시각을 붙여 새 파일을 만듭니다.

## Windows에서 빌드·확인

- .NET 10 SDK / Visual Studio의 .NET 데스크톱 개발 환경을 사용합니다.
- `TimeTableManager.sln` 열기 → `TimeTableManager.App` 시작 프로젝트 → 빌드·실행.
- `verify.cmd`: 기간 경계·저장·교체를 포함한 회귀 사례 실행.
- `build_release.cmd`: 자체 포함 Windows x64 실행 ZIP·Templates ZIP·라이선스·소개·SHA-256 생성. **소스 ZIP은 만들지 않습니다.**
- 4.1.0 배포 결과: `artifacts/release/4.1.0`. 이 폴더의 실행·템플릿 ZIP과 해시 파일을 배포합니다.
- 같은 버전 재생성은 `build_release.cmd -Force`.

## 공개 소스 내보내기

공개할 소스가 필요할 때에만 `export_public_source.cmd`를 직접 실행합니다. 결과는 `artifacts/source/<버전>`에 생성하며 실행 배포 폴더와 분리합니다.

`tools/PublicSourceFiles.json`에 명시된 파일만 내보냅니다. 같은 목록으로 Git 추적 허용 목록을 갱신합니다. 기존에 추적한 불필요한 파일은 `.gitignore`만 바꾸어도 제거되지 않으므로 별도로 추적을 해제해야 합니다. 웹페이지·개인 안내·내부 점검 자료는 공개 소스에 포함하지 않습니다.

## 버전·사용 조건

- [CHANGELOG.md](CHANGELOG.md) / [VERSION_HISTORY.txt](VERSION_HISTORY.txt): 버전별 변경 내역.
- 정식 배포는 [GitHub Releases](https://github.com/lottoria-dev/SmartTimeTableManager4/releases)에서 확인합니다.
- 태그는 `4.1.0`처럼 `v` 없이 버전명만 사용합니다.
- 교육기관 교직원의 학급 운영·업무 목적 무료 사용. 상업적 영리 목적 사용 금지.
- [LICENSE.md](LICENSE.md), [LICENSE_ORIGINAL.txt](LICENSE_ORIGINAL.txt), [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 참조.

문의: [mathtime.ai@gmail.com](mailto:mathtime.ai@gmail.com)
