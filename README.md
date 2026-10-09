# Smart Timetable Manager 4

교사가 큰 시간표에서 수업을 조회하고, 클릭·드래그로 교체·보강을 처리하는 Windows 프로그램입니다. 한국 학교의 장판지식 수업 교체 업무에 집중합니다.

**현재 버전: 4.0.3** · 개발: **MathTime** · [공식 안내](https://mathtime.kr/?page=timetable)

4.0.3: 도움말에 버전 히스토리 추가. 동일셀·분리셀 Excel 템플릿과 CSV 예제를 함께 제공합니다.

본 프로그램은 개발자가 기능을 기획하고, 일부 코드 작성·검토 및 문서화 과정에서 **생성형 AI를 활용했습니다.**

## 주요 기능

- 조회 / 맞교환 / 보강 / 수동 교체 / AI 자동의 다섯 모드.
- 교사·학급 전체 표, 교사 수업 강조, 변경된 행만 조회.
- 지정한 목적 시간으로 자동 교체. 당일 우선, 필요하면 이전·이후 날짜로 확대.
- 대기 카드를 순서대로 배치하는 수동 연쇄 교체와 추천 시간 표시.
- 교장·교감 특별 보강, 원래·새 담당 교사 표시.
- 하루·오전 수업 비우기: 배치 우선 → 연강 확인 → 필요한 수업만 수동 조정.
- 수업·교사·학년별 잠금, 날짜별 학년 행사 제외.
- 기초 요일 일과 적용, 월 관리·월 경계 이동·결보강 통계.
- 변경 다이어그램, 실행 취소·다시 실행, 자동 복구, Excel·CSV 내보내기.

자동 교체는 PC 안에서 수행하는 제한 탐색입니다. 외부 AI API에 시간표를 전송하지 않습니다. 탐색 실패는 교체 불가능의 증명이 아니며 최소 변경의 전역 최적해를 보장하지 않습니다.

## 빠른 시작

1. [4.0.3 Release](https://github.com/lottoria-dev/SmartTimeTableManager4/releases/tag/4.0.3)에서 실행 ZIP을 내려받아 압축을 풉니다.
2. `TimeTableManager.exe` 실행 → 파일 → 시간표 파일 열기.
3. `templates`의 동일셀·분리셀 Excel 중 하나를 학교 자료로 편집해 엽니다. `.ttm`으로 저장한 작업을 이어서 열 수 있습니다.
4. 관리 월·편집 허용 시작일 확인 → 모드 선택 → 교체안 확인·적용 → Ctrl+S.

자체 포함 실행 ZIP은 Windows x64용입니다. 개발용 소스는 .NET 10 / C# 14 / WPF입니다. Visual Studio 실행 방법은 [VISUAL_STUDIO_GUIDE.txt](VISUAL_STUDIO_GUIDE.txt)에 있습니다.

## 기본 템플릿

- [동일셀 Excel](templates/기초시간표_동일셀.xlsx): 교과명과 교사명을 한 셀에 줄바꿈으로 입력.
- [분리셀 Excel](templates/기초시간표_분리셀.xlsx): 교과 행 바로 아래에 교사 행 배치. 교사 행의 학년·반은 비워 둡니다.
- 두 양식 모두 지원하며 예제 데이터도 같습니다. 17개 학급·37명 교사·1주 544개 수업. 교사명은 모두 가상 이름입니다.
- [템플릿 사용방법](templates/템플릿_사용방법.txt): 편집·가져오기 안내. 같은 구조의 UTF-8 CSV도 동봉합니다.
- 실행 ZIP에도 `templates` 폴더를 넣습니다. 템플릿만 필요한 경우 Release의 Templates ZIP을 사용하세요.

## 학교 상황별 활용

| 상황 | 처리 순서 |
|---|---|
| 지각·회의로 1교시를 6교시로 이동 | AI 자동 → 시작 수업·6교시 지정 → 교체안 확인·적용 |
| 갑작스러운 결근 | 보강 → 공강 교사 선택 → 연강 확인·배정 |
| 출장으로 하루 또는 오전 비우기 | 수업 우클릭 → 하루 이동 → 날짜 범위 선택 → 배치·확인 |
| AI 배치 후 특정 교사의 시간 조정 | AI 안 적용 → 수동 교체 → 재변경 확인 → 대기 카드 모두 배치 |
| 특정 학년 행사 | 날짜 제목 우클릭 → 해당 학년 제외 |
| 월요일에 목요일 일과 운영 | 날짜 제목 우클릭 → 기초 요일 적용 → 목요일 |

도움말 → 학교 상황별 활용에서 짧은 시나리오를 확인할 수 있습니다.

## 저장·통계

- `.ttm`에는 기초·현재 배정, 잠금, 날짜, 행사 제외와 변경 이력을 보존합니다.
- 다음 월은 최신 작업 파일에서 관리 월을 변경합니다. 독립 편집 파일 자동 병합은 제공하지 않습니다.
- 월 통계는 학교 계획을 반영한 기초 배정과 현재 배정의 최종 차이입니다. 실제 실시·미실시 기록과는 별개입니다.
- 기초 요일 적용은 학교 계획으로 관리합니다. 교체 이력·변동 색상에서 제외하며 Ctrl+Z 대상이 아닙니다. 바꾸려면 기초 요일을 다시 적용합니다.
- 기존 `.ttm` 형식 1~5를 읽습니다. 학교 계획 등 필요한 경우 형식5로 저장합니다. 이전 프로그램에서 새 형식 파일을 열 수 없으므로 원본을 보관하세요.

## 새 저장소·릴리즈

- 저장소: [lottoria-dev/SmartTimeTableManager4](https://github.com/lottoria-dev/SmartTimeTableManager4)
- 이전 `SmartTimeTableManagerPro-Source`와 별도의 Git 이력으로 관리합니다.
- 기본 브랜치 `main`, 현재 태그 **`4.0.3`**. 이후 `4.0.4`, `4.1.0`처럼 사용하며 `v`를 붙이지 않습니다.
- [GITHUB_SETUP.txt](GITHUB_SETUP.txt): 새 저장소 등록·태그·릴리즈 순서.
- 도움말 → 버전 히스토리에서 오프라인 조회. [CHANGELOG.md](CHANGELOG.md)와 [VERSION_HISTORY.txt](VERSION_HISTORY.txt)에도 같은 변경 내역을 제공합니다.
- [RELEASE_NOTES.md](RELEASE_NOTES.md): Release 본문.
- [docs/RELEASE_CHECKS.txt](docs/RELEASE_CHECKS.txt): Windows 확인 목록.

이 묶음에서는 소스 정적 점검만 수행했습니다. 컴파일·회귀 실행·Windows 화면 확인·EXE 게시는 실행하지 않았습니다.

## 사용 조건·외부 구성요소

교육기관 교직원의 학급 운영·업무 목적 무료 사용을 허용하며 상업적 영리 목적 사용을 금지합니다. [LICENSE.md](LICENSE.md), 원본 고지 [LICENSE_ORIGINAL.txt](LICENSE_ORIGINAL.txt), [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)를 확인하세요.

문의: [mathtime.ai@gmail.com](mailto:mathtime.ai@gmail.com)
