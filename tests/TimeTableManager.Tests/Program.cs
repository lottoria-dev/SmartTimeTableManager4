using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using TimeTableManager.Application;
using TimeTableManager.App;
using TimeTableManager.Domain;
using TimeTableManager.Infrastructure;

var results = new List<TestResult>();
var metrics = new Dictionary<string, double>();
var temporary = Path.Combine(Path.GetTempPath(), "ttm-alpha1-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
void Check(bool condition, string message = "assertion failed") { if (!condition) throw new InvalidOperationException(message); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}
void Test(string id, string name, Action action)
{
    var timer = Stopwatch.StartNew();
    try { action(); results.Add(new(id, name, true, timer.Elapsed.TotalMilliseconds, null)); Console.WriteLine($"PASS {id} {name}"); }
    catch (Exception ex) { results.Add(new(id, name, false, timer.Elapsed.TotalMilliseconds, ex.ToString())); Console.WriteLine($"FAIL {id} {name}: {ex.Message}"); }
}
ScheduleState State() => new(
    [new("t1", "교사A"), new("t2", "교사B"), new("t3", "교사C"), new("t4", "교사D")],
    [new("c1", "1-1", "t1"), new("c2", "1-2", "t2")],
    Enumerable.Range(1, 8).Select(p => new TimeSlot("s" + p, 1, 1, p)).Concat([new TimeSlot("s9", 2, 3, 8)]).ToImmutableArray(),
    [new("l1", "c1", "수학", "t1", "s1", Source: "R1"), new("l2", "c1", "국어", "t2", "s2", Source: "R2"),
     new("l3", "c2", "수학", "t1", "s2", Source: "R3"), new("l4", "c2", "과학", "t3", "s1", Source: "R4")], []);
ProjectSession Session(ScheduleState? state = null, bool saved = true) => new(ProjectDocument.Create("합성 시간표", state ?? State()), saved);
// A fixture that displaces another teacher; own-lesson moves are reviewed separately.
ScheduleState AutoChainState() => State() with { Lessons = State().Lessons.Select(x => x.Id == "l3"
    ? x with { TeacherId = "t2", SlotId = "s1" } : x.Id == "l4" ? x with { SlotId = "s2" } : x).ToImmutableArray() };
void ApplyMove(ProjectSession session, string lesson = "l1", string slot = "s3") => Check(session.Apply(ChangePlanner.Move(session, lesson, slot)).IsValid);
string Json(ProjectDocument document) => JsonSerializer.Serialize(document, ProjectRepository.JsonOptions);
var legacy = new LegacyCsvImporter();
var rowsImporter = new LongTableImporter();
ImportPreview Rows(string text) { var table = TabularReader.ReadCsvText(text); return rowsImporter.Parse(table, LongTableImporter.Suggest(table)); }

Test("A001", "교사 충돌: 미리보기와 실제 적용 모두 거부", () => {
    var s = Session(); var before = Json(s.Document); var p = ChangePlanner.Move(s, "l1", "s2");
    Check(!s.Preview(p).IsValid); Check(!s.Apply(p).IsValid); Check(Json(s.Document) == before);
});
Test("A002", "정상 이동 후 수업 ID 집합 보존", () => {
    var s = Session(); var ids = s.Document.Current.Lessons.Select(x => x.Id).ToHashSet(); ApplyMove(s);
    Check(ids.SetEquals(s.Document.Current.Lessons.Select(x => x.Id))); Check(s.Document.Current.Lessons.Single(x => x.Id == "l1").SlotId == "s3");
});
Test("A003", "미리보기 폐기 시 이전 확정 이력 유지", () => {
    var s = Session(); ApplyMove(s); var before = Json(s.Document); _ = s.Preview(ChangePlanner.Move(s, "l2", "s4")); Check(Json(s.Document) == before); Check(s.Document.HistoryCursor == 1);
});
Test("A004", "Undo/Redo 한 번으로 맞교환 전체 복원", () => {
    var state = State() with { Lessons = State().Lessons.Where(x => x.ClassId == "c1").ToImmutableArray() };
    var s = Session(state); var before = s.Document.Current; ApplyMove(s, "l1", "s2"); var after = s.Document.Current;
    Check(s.Undo()); Check(DocumentIntegrity.StateEquals(before, s.Document.Current)); Check(s.Redo()); Check(DocumentIntegrity.StateEquals(after, s.Document.Current));
});
Test("A005", "5개 수업 연쇄 변경을 한 작업으로 Undo", () => {
    var state = State() with { Lessons = Enumerable.Range(1,5).Select(i => new LessonInstance("n"+i,"c1","과목"+i,"t1","s"+i)).ToImmutableArray() };
    var s = Session(state);
    var p = new ChangePlan(s.Revision, ChangeKind.Move, "5개 연쇄", state.Lessons.Select(x => new LessonDelta(x,x with { SlotId = "s"+(int.Parse(x.SlotId[1..])%5+1) })).ToImmutableArray());
    Check(s.Apply(p).IsValid); Check(s.Document.HistoryCursor == 1); Check(s.Undo()); Check(DocumentIntegrity.StateEquals(state,s.Document.Current)); Check(s.Redo());
});
Test("A006", "저장 후 Undo는 미저장, Redo는 저장됨", () => {
    var s=Session(); ApplyMove(s); s.MarkSaved(s.Document.CurrentStateId); Check(!s.IsDirty); s.Undo(); Check(s.IsDirty); s.Redo(); Check(!s.IsDirty);
});
Test("A007", "초기 저장 지점으로 Undo하면 저장됨", () => { var s=Session(); ApplyMove(s); Check(s.IsDirty); s.Undo(); Check(!s.IsDirty); });
Test("A008", "Undo 후 새 작업은 Redo 분기 제거", () => { var s=Session(); ApplyMove(s); s.Undo(); ApplyMove(s,"l1","s4"); Check(!s.CanRedo); Check(s.Document.History.Length==1); });
Test("A009", "상태가 바뀐 미리보기 거부", () => { var s=Session(); var p=ChangePlanner.Move(s,"l1","s3"); Check(s.Apply(ChangePlanner.ToggleLock(s,"l2")).IsValid); Check(!s.Apply(p).IsValid); });
Test("A010", "Undo로 원위치여도 이전 미리보기 거부(ABA)", () => { var s=Session(); var p=ChangePlanner.Move(s,"l1","s3"); ApplyMove(s); s.Undo(); Check(!s.Apply(p).IsValid); });
Test("A011", "잠금은 변경 이력·저장 상태에 반영", () => { var s=Session(); Check(s.Apply(ChangePlanner.ToggleLock(s,"l1")).IsValid); Check(s.IsDirty); Check(!s.Apply(ChangePlanner.Move(s,"l1","s3")).IsValid); s.Undo(); Check(!s.IsDirty); });
Test("A012", "잠긴 상대 수업과 맞교환 거부", () => { var s=Session(State() with { Lessons=State().Lessons.Where(x=>x.ClassId=="c1").ToImmutableArray() }); Check(s.Apply(ChangePlanner.ToggleLock(s,"l2")).IsValid); Check(!s.Apply(ChangePlanner.Move(s,"l1","s2")).IsValid); });
Test("A013", "교사 변경 중복 차단(직책 예외 없음)", () => { var s=Session(); Check(!s.Apply(ChangePlanner.AssignTeacher(s,"l1","t3")).IsValid); Check(s.Apply(ChangePlanner.AssignTeacher(s,"l1","t4")).IsValid); });
Test("A014", "미배정 수업 보존 및 담당 연결", () => { var state=State() with { Lessons=State().Lessons.SetItem(0,State().Lessons[0] with {TeacherId=null}) }; var s=Session(state); Check(!s.Apply(ChangePlanner.Move(s,"l1","s3")).IsValid); Check(s.Apply(ChangePlanner.AssignTeacher(s,"l1","t4")).IsValid); });
Test("A015", "8교시·부분 2주 이동", () => { var s=Session(); ApplyMove(s,"l1","s9"); Check(s.Document.Current.Lessons[0].SlotId=="s9"); Check(s.Document.History[0].Changes.Length==1); });
Test("A016", "교실 충돌 차단", () => { var state=State() with { Lessons=State().Lessons.SetItem(0,State().Lessons[0] with {Room="R"}).SetItem(2,State().Lessons[2] with {Room="R"}) }; var s=Session(state); Check(s.Apply(ChangePlanner.Move(s,"l4","s2")).Issues.Any(x=>x.Code=="ROOM_COLLISION")); });
Test("A017", "부재 교사 배정 거부", () => { var s=Session(State() with {Blocks=[new("t4","s1")]}); Check(!s.Apply(ChangePlanner.AssignTeacher(s,"l1","t4")).IsValid); });
Test("A018", "명령 위조로 수업 ID·과목 변경 거부", () => { var s=Session(); var l=s.Document.Current.Lessons[0]; Check(!s.Apply(new(s.Revision,ChangeKind.Move,"bad",[new(l,l with {Id="other",SlotId="s3"})])).IsValid); Check(!s.Apply(new(s.Revision,ChangeKind.Move,"bad",[new(l,l with {Subject="other",SlotId="s3"})])).IsValid); });
Test("A019", "같은 수업의 중복 변경 거부", () => { var s=Session(); var l=s.Document.Current.Lessons[0]; var d=new LessonDelta(l,l with{SlotId="s3"}); Check(!s.Apply(new(s.Revision,ChangeKind.Move,"bad",[d,d])).IsValid); });
Test("A020", "후보 생성과 실제 적용의 동일 검증", () => { var s=Session(); var candidates=ChangePlanner.DirectCandidates(s,"l1"); Check(candidates.All(x=>x.Slot.Id!="s2")); Check(candidates.Any(x=>x.Slot.Id=="s3")); foreach(var c in candidates) Check(s.Preview(c.Plan).IsValid); });
Test("A021", "후보 탐색 취소 시 원본 불변", () => { var s=Session(); var before=Json(s.Document); using var c=new CancellationTokenSource(); c.Cancel(); Throws<OperationCanceledException>(()=>ChangePlanner.DirectCandidates(s,"l1",c.Token)); Check(Json(s.Document)==before); });
Test("A022", "헤더만 있는 기존 CSV는 실패", () => { var p=legacy.Parse(TabularReader.ReadCsvText("학년,반,월\n,,1\n")); Check(!p.CanImport); });
Test("A023", "잘못된 학급 행을 조용히 버리지 않음", () => { var p=legacy.Parse(TabularReader.ReadCsvText("학년,반,월\n,,1\n잘못,학급,과학\n")); Check(!p.CanImport); });
Test("A024", "구버전 메타데이터 파일은 명시적으로 차단", () => { var p=legacy.Parse(TabularReader.ReadCsvText("학반,월1\n1-1,수학\n#METADATA_LOCKED_v1,broken\n")); Check(!p.CanImport); Check(p.Issues[0].Code=="LEGACY_METADATA"); });
Test("A025", "교사 없는 과목 셀 보존", () => { var p=legacy.Parse(TabularReader.ReadCsvText("학반,월1\n1-1,창체\n")); Check(p.CanImport); Check(p.Document!.Current.Lessons.Length==1); Check(p.Document.Current.Lessons[0].TeacherId is null); });
Test("A026", "빈 요일·부분 2주·8교시 보존", () => { var p=legacy.Parse(TabularReader.ReadCsvText("학반,1주 월8,2주 수8\n1-1,창체,자율\n")); Check(p.CanImport); Check(p.Document!.Current.Slots.Length==2); Check(p.Document.Current.Lessons.Length==2); });
Test("A027", "1주 자료는 임의로 2주 복제하지 않음", () => { var t=TabularReader.ReadCsvText("학반,월1\n1-1,수학\n"); Check(legacy.Parse(t).Document!.Current.Lessons.Length==1); Check(legacy.Parse(t,repeatSingleWeek:true).Document!.Current.Lessons.Length==2); });
Test("A028", "이미 2주인 자료의 반복 생성 거부", () => { var t=TabularReader.ReadCsvText("학반,1주 월1,2주 월1\n1-1,수학,과학\n"); Check(!legacy.Parse(t,repeatSingleWeek:true).CanImport); });
Test("A029", "CSV 따옴표·쉼표·줄바꿈", () => { var t=TabularReader.ReadCsvText("a,b\n\"인용 \"\"문자\"\"\",\"수학,과학\n교사A\"\n"); Check(t.Rows[1][0]=="인용 \"문자\""); Check(t.Rows[1][1]=="수학,과학\n교사A"); });
Test("A030", "손상된 CSV 따옴표 거부", () => Throws<InvalidDataException>(()=>TabularReader.ReadCsvText("a,b\n\"broken,b\n")));
Test("A031", "범용 열 연결 및 교사 코드 분리", () => { var p=Rows("학급,요일,교시,과목,교사,교사코드\n1-1,월,1,수학,동명,T1\n1-2,월,1,수학,동명,T2\n"); Check(p.CanImport); Check(p.Document!.Current.Teachers.Length==2); });
Test("A032", "날짜 열을 주기 요일로 오인하지 않음", () => { Check(!Rows("학급,요일,교시,과목\n1-1,20260914,1,수학\n").CanImport); });
Test("A033", "같은 교사 코드의 이름 불일치 거부", () => { Check(!Rows("학급,요일,교시,과목,교사,교사코드\n1-1,월,1,수학,A,T1\n1-1,월,2,수학,B,T1\n").CanImport); });
Test("A034", "필수 열 중복 연결 차단", () => { var t=TabularReader.ReadCsvText("a,b,c,d\n1-1,월,1,수학\n"); Check(!rowsImporter.Parse(t,new(0,0,1,2,0)).CanImport); });
Test("A035", "저장 양식의 헤더 변경 감지", () => { var a=TabularReader.ReadCsvText("학급,요일,교시,과목\n1-1,월,1,수학\n"); var p=LongTableImporter.Suggest(a); var b=TabularReader.ReadCsvText("학급,요일,과목,교시\n1-1,월,수학,1\n"); Check(!rowsImporter.Parse(b,p).CanImport); });
Test("A036", "가져오기 실패가 열린 프로젝트를 바꾸지 않음", () => { var s=Session(); var before=Json(s.Document); Check(!Rows("학급,요일,교시,과목\n1-1,월,999,수학").CanImport); Check(Json(s.Document)==before); });
Test("A037", "SQLite 왕복: 원본·현재·담임·잠금·Undo/Redo", () => {
    var s=Session(); ApplyMove(s); Check(s.Apply(ChangePlanner.ToggleLock(s,"l1")).IsValid); s.Undo();
    var path=Path.Combine(temporary,"왕복.ttm"); using(var file=new ProjectFile(path)) file.Save(s.Document);
    using var reopened=new ProjectFile(path); var doc=reopened.Load(); Check(Json(doc)==Json(s.Document)); var loaded=new ProjectSession(doc,true); Check(loaded.Redo()); Check(loaded.Document.Current.Lessons[0].Locked); Check(loaded.Undo());
});
Test("A038", "프로젝트 저장에 미리보기는 포함되지 않음", () => { var s=Session(); var before=Json(s.Document); _=s.Preview(ChangePlanner.Move(s,"l1","s3")); var path=Path.Combine(temporary,"draft.ttm"); ProjectRepository.WriteAtomic(path,s.Document); Check(Json(ProjectRepository.Read(path))==before); });
Test("A039", "저장 실패 시 기존 프로젝트·저장 상태 보존", () => { var s=Session(); var path=Path.Combine(temporary,"protected.ttm"); using var f=new ProjectFile(path); f.Save(s.Document); var bytes=File.ReadAllBytes(path); ApplyMove(s); Throws<InvalidDataException>(()=>f.Save(s.Document with{SchemaVersion=99})); Check(File.ReadAllBytes(path).SequenceEqual(bytes)); Check(s.IsDirty); });
Test("A040", "이전 정상 저장본 백업", () => { var s=Session(); var path=Path.Combine(temporary,"backup.ttm"); using var f=new ProjectFile(path); f.Save(s.Document); var before=Json(s.Document); ApplyMove(s); f.Save(s.Document); Check(Json(ProjectRepository.Read(path+".bak"))==before); });
Test("A041", "동일 파일 두 번째 쓰기 잠금", () => { var path=Path.Combine(temporary,"lease.ttm"); using var f=new ProjectFile(path); Throws<IOException>(()=>{using var second=new ProjectFile(path);}); });
Test("A042", "외부 파일 변경 후 저장 거부", () => { var s=Session(); var path=Path.Combine(temporary,"external.ttm"); using var f=new ProjectFile(path); f.Save(s.Document); File.WriteAllText(path,"external"); Throws<IOException>(()=>f.Save(s.Document)); Check(File.ReadAllText(path)=="external"); });
Test("A043", "손상 SQLite 파일 읽기 실패", () => { var path=Path.Combine(temporary,"corrupt.ttm"); File.WriteAllText(path,"broken"); Throws<SqliteException>(()=>ProjectRepository.Read(path)); });
Test("A044", "프로젝트 페이로드 해시 불일치 감지", () => { var path=Path.Combine(temporary,"tamper.ttm"); ProjectRepository.WriteAtomic(path,Session().Document); using(var c=new SqliteConnection($"Data Source={path};Pooling=False")){c.Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE project SET sha256='bad'";cmd.ExecuteNonQuery();} Throws<InvalidDataException>(()=>ProjectRepository.Read(path)); });
Test("A045", "현재 상태와 이력 불일치 거부", () => { var s=Session(); Throws<InvalidDataException>(()=>new ProjectSession(s.Document with {CurrentStateId=Guid.NewGuid()})); });
Test("A046", "교환용 CSV 수식 시작 문자 보호", () => { Check(CsvExporter.Escape("=1+1")=="\"'=1+1\""); Check(CsvExporter.Escape("  @foo").StartsWith("\"'",StringComparison.Ordinal)); });
Test("A047", "범용 CSV 내보내기 재읽기 수업 보존", () => { var state=State(); var path=Path.Combine(temporary,"export.csv"); CsvExporter.Write(path,state); var t=TabularReader.Read(path)[0]; var p=rowsImporter.Parse(t,LongTableImporter.Suggest(t)); Check(p.CanImport); Check(p.Document!.Current.Lessons.Length==state.Lessons.Length); });
Test("A048", "XLSX 다중 시트·병합 헤더 읽기", () => {
    var path=Path.Combine(temporary,"merged.xlsx"); using(var w=new XLWorkbook()){var s=w.AddWorksheet("기초");s.Cell(1,1).Value="학반";s.Range(1,2,1,3).Merge().Value="월";s.Cell(2,2).Value=1;s.Cell(2,3).Value=8;s.Cell(3,1).Value="1-1";s.Cell(3,2).Value="수학\n교사A";s.Cell(3,3).Value="창체";w.AddWorksheet("빈시트");w.SaveAs(path);}
    var tables=TabularReader.Read(path); Check(tables.Length==1); var p=legacy.Parse(tables[0]); Check(p.CanImport); Check(p.Document!.Current.Lessons.Length==2);
});
Test("A049", "XLSX 수식 파일 명시적 거부", () => { var path=Path.Combine(temporary,"formula.xlsx");using(var w=new XLWorkbook()){var s=w.AddWorksheet("sheet");s.Cell(1,1).FormulaA1="1+1";w.SaveAs(path);}Throws<InvalidDataException>(()=>TabularReader.Read(path)); });
Test("A050", "XLS 파일 지원으로 오인하지 않음", () => { var path=Path.Combine(temporary,"old.xls");File.WriteAllText(path,"dummy");Throws<InvalidDataException>(()=>TabularReader.Read(path)); });
Test("A051", "중복 슬롯·중복 수업 ID 거부", () => { var state=State();Check(!ConstraintValidator.Validate(state with{Slots=state.Slots.Add(state.Slots[0] with{Id="duplicate"})}).IsValid);Check(!ConstraintValidator.Validate(state with{Lessons=state.Lessons.Add(state.Lessons[0])}).IsValid); });
Test("A052", "반복 이동 100회와 전체 Undo 원상 복구", () => { var s=Session();var start=s.Document.Current;for(int i=0;i<100;i++)ApplyMove(s,"l1",i%2==0?"s3":"s4");for(int i=0;i<100;i++)Check(s.Undo());Check(DocumentIntegrity.StateEquals(start,s.Document.Current));Check(!s.CanUndo); });

Test("A053", "화면 모델: 선택 취소가 확정 이력을 되돌리지 않음", () => { var s=Session();ApplyMove(s);var vm=new MainViewModel();vm.Attach(s);vm.Select("l2");vm.ShowPlan(ChangePlanner.Move(s,"l2","s4"));vm.CancelPreview();Check(s.Document.HistoryCursor==1);Check(!vm.CanApply); });
Test("A054", "화면 모델: 수업 선택 변경 시 이전 미리보기 무효화", () => {var s=Session();var vm=new MainViewModel();vm.Attach(s);vm.Select("l1");vm.ShowPlan(ChangePlanner.Move(s,"l1","s3"));Check(vm.CanApply);vm.Select("l2");Check(!vm.CanApply);});
Test("A055", "화면 모델: 미배정·동명이인 표시", () => { var state=State() with{Teachers=[new("t1","동명"),new("t2","동명"),new("t3","교사C"),new("t4","교사D")],Lessons=State().Lessons.SetItem(0,State().Lessons[0] with {TeacherId=null})};var vm=new MainViewModel();vm.Attach(Session(state));Check(vm.Teachers.Where(x=>x.Label.Contains("동명",StringComparison.Ordinal)).Select(x=>x.Label).Distinct().Count()==2);vm.Select("l1");Check(vm.SelectedSummary.Contains("미배정",StringComparison.Ordinal)); });
Test("A056", "작업 이력의 명령 종류 위조 감지", () => {var s=Session();ApplyMove(s);var op=s.Document.History[0] with{Kind=ChangeKind.AssignTeacher};Throws<InvalidDataException>(()=>new ProjectSession(s.Document with{History=[op]}));});
Test("A057", "복구 경로에 쓰이는 프로젝트 ID 검증", () => {var s=Session();Throws<InvalidDataException>(()=>new ProjectSession(s.Document with{ProjectId="../../bad"}));});
Test("A058", "파일 교체 실패 시 직전 저장본 보존", () => {var s=Session();var path=Path.Combine(temporary,"replace-fail.ttm");using var f=new ProjectFile(path);f.Save(s.Document);var before=File.ReadAllBytes(path);Directory.CreateDirectory(path+".bak");ApplyMove(s);Throws<IOException>(()=>f.Save(s.Document));Check(File.ReadAllBytes(path).SequenceEqual(before));Check(s.IsDirty);Check(!Directory.EnumerateFiles(temporary,"replace-fail.ttm.tmp-*").Any());});

Test("A059", "병합 수업을 임의로 복제하지 않고 거부", () => { var path=Path.Combine(temporary,"merged-lesson.xlsx");using(var w=new XLWorkbook()){var s=w.AddWorksheet("기초");s.Cell(1,1).Value="학반";s.Cell(1,2).Value="월1";s.Cell(1,3).Value="월2";s.Cell(2,1).Value="1-1";s.Range(2,2,2,3).Merge().Value="수학\n교사A";w.SaveAs(path);}Throws<InvalidDataException>(()=>TabularReader.Read(path)); });

// r2 추가 사례: 이번 제공본에서는 실행하지 않음. Windows에서 확인할 것.
Test("R201", "자동 연쇄 교체는 원본 불변이며 적용 시 공통 검증 통과", () => {
    var s=Session();var before=Json(s.Document);var scope=s.Document.Current.Slots.Select(x=>x.Id).ToHashSet();
    var r=SwapSearch.Find(s,"l1","s2",scope,CancellationToken.None);
    Check(r.Plan is not null,r.Message);Check(Json(s.Document)==before);Check(s.Apply(r.Plan!).IsValid);
    Check(s.Document.Current.Lessons.Single(x=>x.Id=="l1").SlotId=="s2");Check(s.Undo());Check(DocumentIntegrity.StateEquals(State(),s.Document.Current));
});
Test("R202", "자동 탐색 사전 취소는 원본 유지", () => {var s=Session();var before=Json(s.Document);using var c=new CancellationTokenSource();c.Cancel();Throws<OperationCanceledException>(()=>SwapSearch.Find(s,"l1","s3",s.Document.Current.Slots.Select(x=>x.Id).ToHashSet(),c.Token));Check(Json(s.Document)==before);});
Test("R203", "자동 탐색 범위 밖 목적 거부", () => {var s=Session();var r=SwapSearch.Find(s,"l1","s9",new HashSet<string>{"s1","s2"},CancellationToken.None);Check(r.Plan is null);});

// r3: 표준 XLSX 병합과 표시용 그룹 전환 회귀 사례. 이번 제공본에서는 실행하지 않음.
foreach (bool paired in new[] { false, true })
{
    Test(paired ? "R302" : "R301", paired ? "분리셀 XLSX의 학급·담임 병합과 교사 행" : "동일셀 XLSX의 학년·헤더 병합과 줄바꿈", () => {
        var path = Path.Combine(temporary, paired ? "standard-paired.xlsx" : "standard-combined.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("전체 시간표");
            int start = paired ? 2 : 3, homeroom = start + 2;
            sheet.Range(1, 1, 2, 1).Merge().Value = paired ? "학반" : "학년";
            if (!paired) sheet.Range(1, 2, 2, 2).Merge().Value = "반";
            sheet.Range(1, start, 1, start + 1).Merge().Value = "월";
            sheet.Cell(2, start).Value = 1; sheet.Cell(2, start + 1).Value = 2;
            sheet.Range(1, homeroom, 2, homeroom).Merge().Value = "담임";
            if (!paired) sheet.Range(3, 1, 4, 1).Merge().Value = 1;
            for (int cls = 0; cls < 2; cls++)
            {
                int row = paired ? 3 + cls * 2 : 3 + cls;
                if (paired) sheet.Range(row, 1, row + 1, 1).Merge().Value = "1-" + (cls + 1);
                else sheet.Cell(row, 2).Value = cls + 1;
                if (paired) sheet.Range(row, homeroom, row + 1, homeroom).Merge().Value = cls == 0 ? "교사A" : "교사B";
                else sheet.Cell(row, homeroom).Value = cls == 0 ? "교사A" : "교사B";
                for (int period = 0; period < 2; period++)
                {
                    bool math = (cls + period) % 2 == 0;
                    string subject = math ? "수학" : "국어", teacher = math ? "교사A" : "교사B";
                    sheet.Cell(row, start + period).Value = paired ? subject : subject + "\r\n" + teacher;
                    if (paired) sheet.Cell(row + 1, start + period).Value = teacher;
                }
            }
            workbook.SaveAs(path);
        }
        var preview = legacy.Parse(TabularReader.Read(path)[0]);
        Check(StandardTimetableReader.Read(path).Single().Current.Lessons.Length == 4);
        Check(preview.CanImport, string.Join("; ", preview.Issues.Select(x => x.Message)));
        var state = preview.Document!.Current;
        Check(state.Classes.Length == 2 && state.Teachers.Length == 2 && state.Lessons.Length == 4 && state.Slots.Length == 2);
        Check(state.Classes.All(x => x.HomeroomTeacherId is not null));
        Check(state.Lessons.All(x => x.TeacherId is not null && (x.Subject is "수학" or "국어")));
        Check(state.Lessons.Count(x => x.Subject == "수학") == 2);
    });
}
Test("R303", "교체 카드 학급별·교사별 전환은 배정·미리보기·이력을 유지", () => {
    var session = Session(); var before = Json(session.Document); var vm = new MainViewModel(); vm.Attach(session);
    vm.Select("l1"); var plan = ChangePlanner.Move(session, "l1", "s2"); vm.ShowPlan(plan);
    Check(vm.PreviewGroups.Count == 1 && vm.PreviewGroups[0].Cards.Count == 2);
    vm.GroupPreviewByTeacher = true;
    Check(vm.PreviewGroups.Count == 2 && vm.PreviewGroups.Sum(x => x.Cards.Count) == 2);
    Check(ReferenceEquals(vm.Pending, plan) && vm.SelectedLessonId == "l1" && Json(session.Document) == before);
    vm.CancelPreview(); Check(vm.PreviewGroups.Count == 0 && Json(session.Document) == before);
});

// r4: 날짜 범위, 주간 경계, 기초 반복, 이전 형식·이력 보존. 이번 제공본에서는 미실행.
Test("R401", "화요일 기준 지난 월요일 제외·다음 월요일과 이번 금요일 양방향 교체", () => {
    var calendar = new ScheduleCalendar(new(2026, 9, 14), new(2026, 9, 15));
    var past = new TimeSlot("past",1,1,1); var friday = new TimeSlot("fri",1,5,1); var monday = new TimeSlot("mon2",2,1,1);
    Check(!calendar.CanEdit(past) && calendar.CanEdit(friday) && calendar.CanEdit(monday));
    Check(CalendarSwapSearch.Allows(calendar,monday,friday,SwapDirection.Earlier));
    Check(CalendarSwapSearch.Allows(calendar,friday,monday,SwapDirection.Later));
    Check(!CalendarSwapSearch.Allows(calendar,monday,past,SwapDirection.Earlier));
    var state = new ScheduleState([new("a","교사A"),new("b","교사B")],[new("c","1-1")],
        [past,friday,monday],[new("source","c","수학","a","mon2"),new("other","c","국어","b","fri")],[]);
    var session=Session(state); session.SetCalendar(calendar); var before=Json(session.Document);
    var scope=state.Slots.Select(x=>x.Id).ToHashSet();
    var result=CalendarSwapSearch.Find(session,"source",null,scope,SwapDirection.Earlier,CancellationToken.None);
    Check(result.Plan is not null,result.Message); Check(Json(session.Document)==before);
    Check(result.Plan!.Changes.Single(x=>x.Before.Id=="source").After.SlotId=="fri");
    Check(!session.Apply(ChangePlanner.Move(session,"source","past")).IsValid);
    Check(session.Apply(result.Plan!).IsValid); Check(session.Undo()); Check(DocumentIntegrity.StateEquals(session.Document.Current,state));
});
Test("R402", "연쇄 교체에서도 기준일 이전 교시를 사용하지 않음", () => {
    var state=new ScheduleState([new("a","교사A"),new("b","교사B"),new("c","교사C")],[new("c1","1-1"),new("c2","1-2")],
        [new("past",1,1,1),new("fri",1,5,1),new("mon2",2,1,1)],
        [new("source","c1","수학","a","mon2"),new("other","c1","국어","b","fri"),
         new("conflict","c2","수학","a","fri"),new("fixed","c2","과학","c","mon2",Locked:true)],[]);
    var session=Session(state);session.SetCalendar(new(new(2026,9,14),new(2026,9,15)));var before=Json(session.Document);
    var r=CalendarSwapSearch.Find(session,"source","fri",state.Slots.Select(x=>x.Id).ToHashSet(),SwapDirection.Earlier,CancellationToken.None);
    Check(r.Plan is null);Check(Json(session.Document)==before);
});
Test("R403", "2주 반복본은 1주 이력 유지·2주 기초 원본 복제·새 프로젝트", () => {
    var state=State() with {Slots=State().Slots.Where(x=>x.Week==1).ToImmutableArray()};
    var session=Session(state);ApplyMove(session);var before=Json(session.Document);
    var copy=ProjectRepetition.CreateTwoWeekCopy(session.Document);Check(Json(session.Document)==before);
    Check(copy.ProjectId!=session.Document.ProjectId && copy.HistoryCursor==1 && copy.Current.Lessons.Length==8);
    Check(copy.Current.Lessons.Select(x=>x.Id).Distinct().Count()==8);
    var second=copy.Current.Lessons.Where(x=>copy.Current.Slots.Single(t=>t.Id==x.SlotId).Week==2).ToArray();
    Check(second.Any(x=>x.Subject=="수학" && x.ClassId=="c1" && copy.Current.Slots.Single(t=>t.Id==x.SlotId).Period==1));
    var replay=new ProjectSession(copy);Check(replay.Undo());Check(DocumentIntegrity.StateEquals(replay.Document.Baseline,replay.Document.Current));
    Throws<InvalidOperationException>(()=>ProjectRepetition.CreateTwoWeekCopy(copy));
});
Test("R404", "날짜 저장·미저장 판정·과거 이력 읽기·기존 날짜 없는 파일 호환", () => {
    var session=Session();ApplyMove(session);session.MarkSaved(session.Document);
    var old=Json(session.Document);var legacyJson=System.Text.Json.Nodes.JsonNode.Parse(old)!.AsObject();legacyJson.Remove("Calendar");
    Check(JsonSerializer.Deserialize<ProjectDocument>(legacyJson.ToJsonString(),ProjectRepository.JsonOptions)!.Calendar is null);
    var oldPlan=ChangePlanner.Move(session,"l1","s4");
    var calendar=new ScheduleCalendar(new(2026,9,14),new(2026,9,15));session.SetCalendar(calendar);
    Check(session.IsDirty && !session.Preview(oldPlan).IsValid);
    var path=Path.Combine(temporary,"calendar.ttm");ProjectRepository.WriteAtomic(path,session.Document);
    var loaded=ProjectRepository.Read(path);Check(loaded.Calendar==calendar && loaded.HistoryCursor==1);
    session.MarkSaved(loaded);Check(!session.IsDirty);
    session.SetCalendar(calendar with{EditableFrom=new(2026,9,16)});Check(session.IsDirty);
    session.MarkSaved(loaded);Check(session.IsDirty);session.SetCalendar(calendar);Check(!session.IsDirty);
    var year=new ScheduleCalendar(new(2026,12,28),new(2026,12,29));
    Check(year.DateOf(new("x",2,1,1))==new DateOnly(2027,1,4) && year.DisplayUntil==new DateOnly(2027,1,8));
});

// r5: 수동 연쇄·추천·보강·맞교환·날짜 허용의 실제 데이터 위험 확인. 미실행 소스.
Test("R501", "수동 밀어넣기는 충돌 카드를 모두 보관하고 완성 후 한 번에 Undo", () => {
    var session=Session();var before=Json(session.Document);var draft=new ManualChainDraft(session);
    Check(draft.Take("l1") is null && draft.Plan() is null);
    Check(draft.Put("s2") is null);Check(draft.Held.Select(x=>x.Id).ToHashSet().SetEquals(new[]{"l2","l3"}));
    Check(Json(session.Document)==before && draft.DisplayState.Lessons.Length+draft.Held.Length==4);
    Check(draft.Take("l2") is null && draft.Put("s3") is null);
    Check(draft.Take("l3") is null && draft.Put("s1") is null);Check(draft.Held.Single().Id=="l4");
    Check(draft.Put("s4") is null);var plan=draft.Plan();Check(plan is not null && plan.Changes.Length==4);
    Check(Json(session.Document)==before);Check(session.Apply(plan!).IsValid && session.Document.HistoryCursor==1);
    Check(session.Undo() && DocumentIntegrity.StateEquals(session.Document.Current,State()));
});
Test("R502", "수동 연쇄 중 되돌리기·잠금·만료 초안은 확정 배정을 바꾸지 않음", () => {
    var session=Session();var draft=new ManualChainDraft(session);var before=Json(session.Document);
    Check(draft.Take("l1") is null && draft.Put("s2") is null);Check(draft.Undo());Check(draft.Held.Single().Id=="l1");
    Check(draft.Undo() && draft.Held.Length==0 && draft.Plan() is null);Check(Json(session.Document)==before);
    Check(session.Apply(ChangePlanner.ToggleLock(session,"l2")).IsValid);draft=new ManualChainDraft(session);
    Check(draft.Take("l1") is null);Check(!draft.Advice("s2").Allowed && draft.Put("s2") is not null);
    Check(session.Apply(ChangePlanner.ToggleLock(session,"l4")).IsValid);Check(draft.Put("s3") is not null && draft.Plan() is null);
});
Test("R503", "공강 보강 후보는 바쁜 교사를 제외하고 3연강 교사를 별표·후순위 표시", () => {
    var state=State() with{Lessons=State().Lessons.Add(new("extra","c1","국어","t2","s3"))};var session=Session(state);
    var choices=TeachingAdvice.CoverCandidates(session,"l1");Check(choices.All(x=>x.TeacherId!="t3"));
    var risk=choices.Single(x=>x.TeacherId=="t2");Check(risk.ThreeConsecutive && risk.Longest==3 && risk.Label.StartsWith("* "));
    Check(choices[0].TeacherId=="t4");var old=state.Lessons.Single(x=>x.Id=="l1");
    Check(session.Apply(ChangePlanner.AssignTeacher(session,"l1","t4")).IsValid);
    var after=session.Document.Current.Lessons.Single(x=>x.Id=="l1");Check(after.SlotId==old.SlotId && after.ClassId==old.ClassId && after.Subject==old.Subject);
    Check(session.Undo() && DocumentIntegrity.StateEquals(session.Document.Current,state));
});
Test("R504", "1대1 맞교환은 정확히 두 수업만 변경하며 추가 충돌은 거부", () => {
    var session=Session();var bad=ChangePlanner.SwapTwo(session,"l1","l2");Check(bad.Changes.Length==2 && !session.Preview(bad).IsValid);
    var state=State() with{Lessons=State().Lessons.Where(x=>x.ClassId=="c1").ToImmutableArray()};session=Session(state);
    var plan=ChangePlanner.SwapTwo(session,"l1","l2");Check(session.Apply(plan).IsValid && session.Document.History.Single().Changes.Length==2);
    Check(session.Undo() && DocumentIntegrity.StateEquals(session.Document.Current,state));
});
Test("R505", "요일 허용은 같은 날을 포함하되 조건 밖의 지정 목적을 대체하지 않음", () => {
    var session=Session();session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));var before=Json(session.Document);
    var scope=session.Document.Current.Slots.Select(x=>x.Id).ToHashSet();
    var result=CalendarSwapSearch.Find(session,"l1","s9",scope,SwapDirection.Earlier,CancellationToken.None);
    Check(result.Plan is null && result.Examined == 0);Check(Json(session.Document)==before);
    Check(CalendarSwapSearch.Find(session,"l1",null,scope,SwapDirection.Earlier,CancellationToken.None).Plan is not null);
    var calendar=session.Document.Calendar!;var slots=session.Document.Current.Slots.ToDictionary(x=>x.Id);
    Check(CalendarSwapSearch.Allows(calendar,slots["s1"],slots["s3"],SwapDirection.SameDay));
    Check(!CalendarSwapSearch.Allows(calendar,slots["s1"],slots["s9"],SwapDirection.SameDay));
});
Test("R506", "연쇄 추천의 공강·3연강 표시와 과거 보강 제외", () => {
    var state=State() with{Lessons=State().Lessons.Add(new("extra","c1","국어","t2","s3")).Add(new("extra2","c1","국어","t2","s5"))};var session=Session(state);
    var draft=new ManualChainDraft(session);Check(draft.Take("l2") is null);
    Check(draft.Advice("s4").Allowed && draft.Advice("s4").ThreeConsecutive && !draft.Advice("s4").Recommended);
    Check(draft.Advice("s6").Recommended);
    session.SetCalendar(new(new(2026,9,14),new(2026,9,15)));Check(TeachingAdvice.CoverCandidates(session,"l1").Count==0);
});
Test("R507", "표준 파일 자동 읽기는 열 연결 없이 입력하고 오류 파일은 거부", () => {
    var path=Path.Combine(temporary,"standard-auto.csv");File.WriteAllText(path,"학반,월1\n1-1,\"수학\n교사A\"\n");
    var documents=StandardTimetableReader.Read(path);Check(documents.Count==1 && documents[0].Current.Lessons.Single().TeacherId is not null);
    File.WriteAllText(path,"잘못된 표\n내용\n");Throws<InvalidDataException>(()=>StandardTimetableReader.Read(path));
});

// r6: 전체 연쇄 날짜 범위와 표시용 이력 회귀 사례. Windows에서 실행할 소스이며 미실행.
foreach (var (caseId, spare, allowed, forbidden) in new[] {
    ("R601a", new TimeSlot("spare",1,1,1), SwapDirection.Earlier, SwapDirection.Later),
    ("R601b", new TimeSlot("spare",1,3,1), SwapDirection.Later, SwapDirection.Earlier),
    ("R601c", new TimeSlot("spare",2,2,1), SwapDirection.Later, SwapDirection.Earlier) })
{
    Test(caseId, "다른 날짜로 보조 수업을 빼야 풀리는 연쇄는 날짜 허용 밖에서 거부", () => {
        var state = new ScheduleState([new("a","교사A"),new("b","교사B"),new("c","교사C")],
            [new("c1","1-1"),new("c2","1-2")], [new("from",1,2,1),new("to",1,2,2),spare],
            [new("source","c1","수학","a","from"),new("other","c1","국어","b","to"),
             new("conflict","c2","수학","b","from"),new("fixed","c2","과학","c","to",Locked:true)], []);
        var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
        var before = Json(session.Document); var visible = state.Slots.Select(x => x.Id).ToHashSet();
        var both = CalendarSwapSearch.Find(session,"source","to",visible,SwapDirection.Both,CancellationToken.None);
        Check(both.Plan is not null && both.Plan.Changes.Any(x => x.After.SlotId == "spare"), both.Message);
        Check(CalendarSwapSearch.Find(session,"source","to",visible,SwapDirection.SameDay,CancellationToken.None).Plan is null);
        Check(CalendarSwapSearch.Find(session,"source","to",visible,forbidden,CancellationToken.None).Plan is null);
        var permitted = CalendarSwapSearch.Find(session,"source","to",visible,allowed,CancellationToken.None);
        Check(permitted.Plan is not null, permitted.Message);
        var slots = state.Slots.ToDictionary(x => x.Id); var calendar = session.Document.Calendar!;
        Check(permitted.Plan!.Changes.All(x => CalendarSwapSearch.Allows(calendar,slots["from"],slots[x.Before.SlotId],allowed)
            && CalendarSwapSearch.Allows(calendar,slots["from"],slots[x.After.SlotId],allowed)));
        Check(Json(session.Document) == before);
    });
}
Test("R602", "같은 날짜 안에서 가능한 연쇄는 모든 참여 수업의 날짜를 유지", () => {
    var state = AutoChainState() with { Slots = State().Slots.Select(x => x.Id == "s9" ? x : x with { Day = 2 }).ToImmutableArray() };
    var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var result = CalendarSwapSearch.Find(session,"l1","s2",state.Slots.Select(x => x.Id).ToHashSet(),SwapDirection.SameDay,CancellationToken.None);
    Check(result.Plan is not null, result.Message);
    var slots = state.Slots.ToDictionary(x => x.Id); var calendar = session.Document.Calendar!;
    var date = calendar.DateOf(slots["s1"]);
    Check(result.Plan!.Changes.All(x => calendar.DateOf(slots[x.Before.SlotId]) == date && calendar.DateOf(slots[x.After.SlotId]) == date));
    Check(session.Preview(result.Plan!).IsValid);
});
Test("R603", "변경 흐름은 잠금을 제외하고 보강·이동과 되돌림 상태만 표시", () => {
    var session = Session();
    Check(session.Apply(ChangePlanner.ToggleLock(session,"l4")).IsValid);
    ApplyMove(session,"l1","s3");
    Check(session.Apply(ChangePlanner.AssignTeacher(session,"l1","t4")).IsValid);
    Check(session.Apply(ChangePlanner.ToggleLock(session,"l2")).IsValid);
    var history = ChangeHistory.Operations(session.Document);
    Check(history.Count == 2 && history.All(x => x.IsApplied) && history.Select(x => x.Number).SequenceEqual(new[] {1,2}));
    var net = ChangeHistory.NetChanges(session.Document);
    Check(net.Length == 1 && net[0].Before.TeacherId == "t1" && net[0].After.TeacherId == "t4" && net[0].After.SlotId == "s3");
    Check(session.Undo() && session.Undo());
    history = ChangeHistory.Operations(session.Document);
    Check(history[0].IsApplied && !history[1].IsApplied && history[1].Operation.Kind == ChangeKind.AssignTeacher);
    Check(session.Undo() && ChangeHistory.NetChanges(session.Document).IsEmpty);
    Check(session.Document.Current.Lessons.Single(x => x.Id == "l4").Locked);
    Check(session.Document.History.Length == 4); // Original lock history remains replayable.
    _ = new ProjectSession(session.Document);
});
Test("R604", "수동 초안 다이어그램은 배치한 이동만 포함하고 대기 수업은 보존", () => {
    var session = Session(); var before = Json(session.Document); var draft = new ManualChainDraft(session);
    Check(draft.Take("l1") is null && draft.PlacedChanges.IsEmpty);
    Check(draft.Put("s2") is null);
    var delta = draft.PlacedChanges.Single();
    Check(delta.Before.Id == "l1" && delta.Before.SlotId == "s1" && delta.After.SlotId == "s2");
    Check(draft.Held.Length == 2 && draft.Plan() is null && Json(session.Document) == before);
    Check(draft.Undo() && draft.PlacedChanges.IsEmpty && draft.Held.Single().Id == "l1");
});

// r7: 명시 목적은 필수 조건. 일괄 잠금과 맞교환 추천은 공통 검증 사용. 미실행 소스.
Test("R701", "1교시를 충돌 있는 6교시로 지정하면 연쇄안도 목적 6교시를 유지", () => {
    var state = AutoChainState() with { Lessons = AutoChainState().Lessons.Select(x => x.SlotId == "s2" ? x with { SlotId = "s6" } : x).ToImmutableArray() };
    var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var visible = state.Slots.Select(x => x.Id).ToHashSet(); var before = Json(session.Document);
    Check(!session.Preview(ChangePlanner.Move(session,"l1","s6")).IsValid);
    for (int attempt = 0; attempt < 2; attempt++)
    {
        var result = CalendarSwapSearch.Find(session,"l1","s6",visible,SwapDirection.SameDay,CancellationToken.None);
        Check(result.Plan is not null, result.Message);
        Check(result.Plan!.Changes.Single(x => x.Before.Id == "l1").After.SlotId == "s6");
        Check(session.Preview(result.Plan!).IsValid && Json(session.Document) == before);
    }
});
Test("R702", "지정 목적 불가·조회 밖·요일 불허 시 다른 빈 교시로 우회하지 않음", () => {
    var state = State() with { Lessons = State().Lessons.Select(x => x.SlotId == "s2" ? x with { SlotId = "s6", Locked = true } : x).ToImmutableArray() };
    var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var visible = state.Slots.Select(x => x.Id).ToHashSet(); var before = Json(session.Document);
    Check(CalendarSwapSearch.Find(session,"l1",null,visible,SwapDirection.SameDay,CancellationToken.None).Plan is not null);
    Check(CalendarSwapSearch.Find(session,"l1","s6",visible,SwapDirection.SameDay,CancellationToken.None).Plan is null);
    Check(CalendarSwapSearch.Find(session,"l1","s9",visible,SwapDirection.SameDay,CancellationToken.None).Plan is null);
    Check(CalendarSwapSearch.Find(session,"l1","missing",visible,SwapDirection.Both,CancellationToken.None).Plan is null);
    Check(CalendarSwapSearch.Find(session,"l1","s1",visible,SwapDirection.Both,CancellationToken.None).Plan is null);
    Check(CalendarSwapSearch.Find(session,"l1","s6",visible,SwapDirection.Both,CancellationToken.None,otherDaysOnly:true).Plan is null);
    visible.Remove("s6");
    Check(CalendarSwapSearch.Find(session,"l1","s6",visible,SwapDirection.Both,CancellationToken.None).Plan is null);
    Check(Json(session.Document) == before);
});
Test("R703", "일괄 잠금은 중복·기존 잠금을 제외하고 한 작업으로 되돌림·재생", () => {
    var state = State() with { Lessons = State().Lessons.Select(x => x.Id == "l2" ? x with { Locked = true } : x).ToImmutableArray() };
    var session = Session(state); var before = Json(session.Document);
    var plan = ChangePlanner.SetLocks(session,new[] {"l1","l1","l2","l4"},true);
    Check(plan.Changes.Length == 2 && Json(session.Document) == before);
    Check(session.Apply(plan).IsValid && session.Document.HistoryCursor == 1);
    Check(session.Document.Current.Lessons.Count(x => x.Locked) == 3 && ChangeHistory.Operations(session.Document).Count == 0);
    var unchanged = Json(session.Document); var noop = ChangePlanner.SetLocks(session,new[] {"l1","l2"},true);
    Check(noop.Changes.IsEmpty && !session.Apply(noop).IsValid && Json(session.Document) == unchanged);
    Throws<KeyNotFoundException>(() => ChangePlanner.SetLocks(session,new[] {"l3","missing"},true));
    Check(Json(session.Document) == unchanged);
    _ = new ProjectSession(session.Document);
    Check(session.Undo() && DocumentIntegrity.StateEquals(session.Document.Current,state));
    Check(session.Redo() && session.Document.Current.Lessons.Count(x => x.Locked) == 3);
    Check(!session.Apply(plan).IsValid);
    Check(session.Apply(ChangePlanner.SetLocks(session,new[] {"l1","l2","l4"},false)).IsValid);
    Check(session.Document.Current.Lessons.All(x => !x.Locked));
    _ = new ProjectSession(session.Document);
});
Test("R704", "맞교환 추천은 실제 두 수업 교환만 검증하며 충돌·잠금·과거·조회 밖 제외", () => {
    var session = Session(); var visible = session.Document.Current.Slots.Select(x => x.Id).ToHashSet();
    var before = Json(session.Document);
    Check(SwapRecommendations.Find(session,"l1",visible,CancellationToken.None).IsEmpty);
    Check(Json(session.Document) == before);
    var state = State() with { Lessons = State().Lessons.Where(x => x.ClassId == "c1").ToImmutableArray() };
    session = Session(state);
    var result = SwapRecommendations.Find(session,"l1",visible,CancellationToken.None);
    Check(result.SetEquals(new[] {"l2"}));
    foreach (var id in result) Check(session.Preview(ChangePlanner.SwapTwo(session,"l1",id)).IsValid);
    Check(SwapRecommendations.Find(session,"l1",new HashSet<string>{"s1"},CancellationToken.None).IsEmpty);
    Check(session.Apply(ChangePlanner.SetLocks(session,new[] {"l2"},true)).IsValid);
    Check(SwapRecommendations.Find(session,"l1",visible,CancellationToken.None).IsEmpty);
    Check(session.Undo());
    session.SetCalendar(new(new(2026,9,14),new(2026,9,15)));
    Check(SwapRecommendations.Find(session,"l1",visible,CancellationToken.None).IsEmpty);
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    Throws<OperationCanceledException>(() => SwapRecommendations.Find(session,"l1",visible,cancellation.Token));
});

// r8: 실제 날짜·다중 학년 제외와 변경 행 필터. Windows 실행용 사례이며 미실행.
ScheduleState GradeState() => State() with {
    Classes = [new("c1", "1-1", "t1"), new("c2", "2학년 1반", "t2")],
    Slots = State().Slots.Add(new("nextMonday", 2, 1, 1))
};
Test("R801", "다중 학년 제외는 출발·빈 목적·보강을 막고 다른 학년·다음 주는 구분", () => {
    var session = Session(GradeState()); session.SetCalendar(new(new(2026,9,14), new(2026,9,14)));
    session.SetGradeDateExclusions([new(new(2026,9,14),1), new(new(2026,9,14),3)]);
    var before = Json(session.Document); var rules = new SchoolDayRules(session.Document);
    Check(rules.IsExcluded("c1","s1") && !rules.IsExcluded("c2","s1") && !rules.IsExcluded("c1","nextMonday"));
    Check(!session.Apply(ChangePlanner.Move(session,"l1","s3")).IsValid);
    Check(!session.Apply(ChangePlanner.AssignTeacher(session,"l1","t4")).IsValid);
    Check(TeachingAdvice.CoverCandidates(session,"l1").Count == 0);
    Check(session.Preview(ChangePlanner.Move(session,"l4","s3")).IsValid && Json(session.Document) == before);
    var state = GradeState() with { Lessons = GradeState().Lessons.Select(x => x.Id == "l1" ? x with { SlotId = "nextMonday" } : x).ToImmutableArray() };
    var incoming = Session(state); incoming.SetCalendar(session.Document.Calendar!);
    incoming.SetGradeDateExclusions(SchoolDayRules.Entries(session.Document));
    Check(!incoming.Preview(ChangePlanner.Move(incoming,"l1","s3")).IsValid);
    Check(SchoolDayRules.GradeOf("3-2") == 3 && SchoolDayRules.GradeOf("2학년 1반") == 2 && SchoolDayRules.GradeOf("특별반") is null);
});
Test("R802", "수동 연쇄는 제외 수업을 꺼내거나 밀어내거나 제외 빈칸에 놓지 않음", () => {
    var session = Session(GradeState()); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    session.SetGradeDateExclusions([new(new(2026,9,14),2), new(new(2026,9,21),1)]);
    var before = Json(session.Document); var draft = new ManualChainDraft(session);
    Check(draft.Take("l3") is not null && draft.Held.IsEmpty);
    Check(draft.Take("l1") is null && !draft.Advice("s2").Allowed && draft.Put("s2") is not null);
    Check(!draft.Advice("nextMonday").Allowed && draft.Put("nextMonday") is not null);
    Check(draft.Held.Single().Id == "l1" && draft.Put("s3") is null);
    Check(draft.Plan() is { } plan && session.Preview(plan).IsValid && plan.Changes.Length == 1);
    Check(Json(session.Document) == before);
});
Test("R803", "자동 연쇄의 보조 수업도 행사 제외를 지키고 목적을 다른 교시로 바꾸지 않음", () => {
    var state = GradeState() with { Lessons = AutoChainState().Lessons,
        Blocks = GradeState().Slots.Where(x => x.Id != "s1" && x.Id != "s2").Select(x => new TeacherBlock("t2",x.Id)).ToImmutableArray() };
    var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var scope = session.Document.Current.Slots.Select(x => x.Id).ToHashSet();
    session.SetGradeDateExclusions([new(new(2026,9,14),2)]);
    var before = Json(session.Document);
    Check(CalendarSwapSearch.Find(session,"l1","s2",scope,SwapDirection.SameDay,CancellationToken.None).Plan is null);
    Check(SwapRecommendations.Find(session,"l3",scope,CancellationToken.None).IsEmpty && Json(session.Document) == before);
    session.SetGradeDateExclusions([]);
    var result = CalendarSwapSearch.Find(session,"l1","s2",scope,SwapDirection.SameDay,CancellationToken.None);
    Check(result.Plan is not null && result.Plan.Changes.Single(x => x.Before.Id == "l1").After.SlotId == "s2");
    var pair = Session(GradeState() with { Lessons = GradeState().Lessons.Where(x => x.ClassId == "c1").ToImmutableArray() });
    pair.SetCalendar(session.Document.Calendar!); pair.SetGradeDateExclusions([new(new(2026,9,14),2)]);
    Check(SwapRecommendations.Find(pair,"l1",scope,CancellationToken.None).SetEquals(new[] {"l2"}));
    pair.SetGradeDateExclusions([new(new(2026,9,14),1)]);
    Check(SwapRecommendations.Find(pair,"l1",scope,CancellationToken.None).IsEmpty);
});
Test("R804", "행사 설정 저장·재열기·이전 파일 호환과 미리보기 만료", () => {
    var session = Session(GradeState()); session.SetCalendar(new(new(2026,9,14),new(2026,9,14))); session.MarkSaved(session.Document);
    var saved = session.Document; var plan = ChangePlanner.Move(session,"l1","s3");
    session.SetGradeDateExclusions([new(new(2026,9,14),2), new(new(2026,9,14),2)]);
    Check(session.IsDirty && session.Document.SchemaVersion == 2 && SchoolDayRules.Entries(session.Document).Length == 1);
    Check(!session.Preview(plan).IsValid); var revision = session.Revision;
    session.SetGradeDateExclusions([new(new(2026,9,14),2)]); Check(session.Revision == revision);
    session.MarkSaved(saved); Check(session.IsDirty); // An older async write cannot mark new settings saved.
    var path = Path.Combine(temporary,"r8-exclusions.ttm"); ProjectRepository.WriteAtomic(path,session.Document);
    var reopened = new ProjectSession(ProjectRepository.Read(path),saved:true);
    Check(!reopened.IsDirty && Json(reopened.Document) == Json(session.Document));
    reopened.SetGradeDateExclusions([]); Check(reopened.IsDirty);
    var node = System.Text.Json.Nodes.JsonNode.Parse(Json(saved))!.AsObject(); Check(node.Remove("GradeDateExclusions"));
    var legacyDocument = JsonSerializer.Deserialize<ProjectDocument>(node.ToJsonString(),ProjectRepository.JsonOptions)!;
    Check(legacyDocument.SchemaVersion == 1 && SchoolDayRules.Entries(new ProjectSession(legacyDocument).Document).IsEmpty);
    Throws<InvalidDataException>(() => session.SetGradeDateExclusions([new(new(2026,9,14),4)]));
    Throws<InvalidDataException>(() => new ProjectSession(saved with { GradeDateExclusions = [new(new(2026,9,14),1)] }));
});
Test("R805", "행사 이후에도 과거 이력은 읽되 제외 수업의 Undo·Redo는 차단", () => {
    var session = Session(GradeState()); session.SetCalendar(new(new(2026,9,14),new(2026,9,14))); ApplyMove(session);
    session.SetGradeDateExclusions([new(new(2026,9,14),1)]); var before = Json(session.Document);
    _ = new ProjectSession(session.Document); Check(!session.CanUndo && !session.Undo() && Json(session.Document) == before);
    Check(session.Apply(ChangePlanner.ToggleLock(session,"l1")).IsValid && session.Undo());
    session.SetGradeDateExclusions([]); Check(session.Undo());
    session.SetGradeDateExclusions([new(new(2026,9,14),1)]); Check(!session.CanRedo && !session.Redo());
    session.SetGradeDateExclusions([]); Check(session.Redo());
    var singleWeek = Session(GradeState() with { Slots = GradeState().Slots.Where(x => x.Week == 1).ToImmutableArray() });
    singleWeek.SetCalendar(session.Document.Calendar!); singleWeek.SetGradeDateExclusions([new(new(2026,9,14),1)]);
    var repeated = ProjectRepetition.CreateTwoWeekCopy(singleWeek.Document); var rules = new SchoolDayRules(repeated);
    Check(repeated.Current.Lessons.Where(x => repeated.Current.Slots.Single(s => s.Id == x.SlotId).Week == 2).All(x => !rules.IsExcluded(x)));
});
Test("R806", "변경 행은 보강의 원래·새 교사와 이동 전후를 포함하고 잠금·원상복귀는 제외", () => {
    var session = Session(); var all = session.Document.Current.Slots.Select(x => x.Id).ToHashSet();
    Check(session.Apply(ChangePlanner.ToggleLock(session,"l4")).IsValid && ChangeHistory.ChangedRows(session.Document,all,true).Count == 0);
    ApplyMove(session,"l1","s3"); Check(session.Apply(ChangePlanner.AssignTeacher(session,"l1","t4")).IsValid);
    Check(ChangeHistory.ChangedRows(session.Document,all,true).SetEquals(new[] {"t1","t4"}));
    Check(ChangeHistory.ChangedRows(session.Document,all,false).SetEquals(new[] {"c1"}));
    Check(ChangeHistory.ChangedRows(session.Document,new HashSet<string>{"s1"},true).SetEquals(new[] {"t1"}));
    Check(ChangeHistory.ChangedRows(session.Document,new HashSet<string>{"s3"},true).SetEquals(new[] {"t4"}));
    Check(ChangeHistory.ChangedRows(session.Document,new HashSet<string>{"s9"},true).Count == 0);
    Check(session.Undo() && session.Undo() && ChangeHistory.ChangedRows(session.Document,all,true).Count == 0);
});

// r9: 무의미한 맞교환·특별 보강·초기화·수동 추천·저장 이름 회귀 소스. 미실행.
Test("R901", "맞교환은 같은 교사를 제외하고 다른 교사의 유효 수업은 유지", () => {
    var state = State() with { Lessons = State().Lessons.Where(x => x.ClassId == "c1").Append(new("own","c1","수학","t1","s3")).ToImmutableArray() };
    var session = Session(state); var before = Json(session.Document); var scope = state.Slots.Select(x => x.Id).ToHashSet();
    Check(SwapRecommendations.Find(session,"l1",scope,CancellationToken.None).SetEquals(new[] {"l2"}));
    var meaningless = ChangePlanner.SwapTwo(session,"l1","own");
    Check(meaningless.Changes.IsEmpty && !session.Apply(meaningless).IsValid && Json(session.Document) == before);
});
Test("R902", "특별 보강 후보 조회는 무변경이고 적용·중복 차단·Undo·저장이 일치", () => {
    var session = Session(); var before = Json(session.Document);
    Check(SpecialCover.Candidates(session,"l1").Select(x => x.TeacherId).ToHashSet().SetEquals(new[] {SpecialCover.Principal,SpecialCover.VicePrincipal}));
    Check(Json(session.Document) == before);
    Check(session.ApplySpecialCover("l1",SpecialCover.Principal,session.Revision).IsValid);
    Check(session.Document.Current.Lessons.Single(x => x.Id == "l1").TeacherId == SpecialCover.Principal);
    var assigned = Json(session.Document);
    Check(!session.ApplySpecialCover("l4",SpecialCover.Principal,session.Revision).IsValid && Json(session.Document) == assigned);
    Check(SpecialCover.Candidates(session,"l4").All(x => x.TeacherId != SpecialCover.Principal));
    Check(session.Undo() && session.Document.Current.Lessons.Single(x => x.Id == "l1").TeacherId == "t1" && session.Redo());
    var vm = new MainViewModel(); vm.Attach(session); Check(vm.Describe(session.Document.Current.Lessons.Single(x => x.Id == "l1")).Contains("교장"));
    var path = Path.Combine(temporary,"r9-special-cover.ttm"); ProjectRepository.WriteAtomic(path,session.Document);
    Check(Json(ProjectRepository.Read(path)) == Json(session.Document));
});
Test("R903", "잠금·행사·만료 선택의 특별 보강은 교사 명부도 변경하지 않음", () => {
    var session = Session(); var stale = session.Revision;
    Check(session.Apply(ChangePlanner.ToggleLock(session,"l1")).IsValid); var locked = Json(session.Document);
    Check(!session.ApplySpecialCover("l1",SpecialCover.Principal,session.Revision).IsValid && Json(session.Document) == locked);
    Check(!session.ApplySpecialCover("l2",SpecialCover.VicePrincipal,stale).IsValid && Json(session.Document) == locked);
    session.SetCalendar(new(new(2026,9,14),new(2026,9,14))); session.SetGradeDateExclusions([new(new(2026,9,14),1)]);
    var excluded = Json(session.Document);
    Check(SpecialCover.Candidates(session,"l2").Count == 0);
    Check(!session.ApplySpecialCover("l2",SpecialCover.VicePrincipal,session.Revision).IsValid && Json(session.Document) == excluded);
    Check(session.Document.Current.Teachers.All(x => !SpecialCover.IsSpecial(x.Id)));
});
Test("R904", "전체 초기화는 최초 수업·잠금 복원, 이력 삭제, 날짜·행사 보존", () => {
    var session = Session(); var initial = session.Document.Baseline;
    session.SetCalendar(new(new(2026,9,14),new(2026,9,14))); ApplyMove(session);
    Check(session.ApplySpecialCover("l1",SpecialCover.Principal,session.Revision).IsValid);
    Check(session.Apply(ChangePlanner.ToggleLock(session,"l2")).IsValid);
    session.SetGradeDateExclusions([new(new(2026,9,14),1)]); session.MarkSaved(session.Document);
    var previous = session.Document; var oldPlan = ChangePlanner.AssignTeacher(session,"l1","t4");
    session.ResetToBaseline();
    Check(DocumentIntegrity.StateEquals(initial,session.Document.Current) && session.Document.History.IsEmpty && session.Document.HistoryCursor == 0);
    Check(!session.CanUndo && !session.CanRedo && session.IsDirty && !session.Preview(oldPlan).IsValid);
    Check(session.Document.Calendar == previous.Calendar && SchoolDayRules.Entries(session.Document).SequenceEqual(SchoolDayRules.Entries(previous)));
    Check(previous.History.Length == 3 && previous.Current.Lessons.Single(x => x.Id == "l1").TeacherId == SpecialCover.Principal);
    var path = Path.Combine(temporary,"r9-reset.ttm"); ProjectRepository.WriteAtomic(path,session.Document);
    Check(Json(ProjectRepository.Read(path)) == Json(session.Document));
});
Test("R905", "꺼낸 자리·이미 대체 수업이 들어간 칸은 추천하지 않고 대기 카드 선택 유지", () => {
    var session = Session(); var before = Json(session.Document); var draft = new ManualChainDraft(session);
    Check(draft.Take("l1") is null && draft.ActiveOriginSlotId == "s1");
    Check(draft.Advice("s1").Allowed && !draft.Advice("s1").Recommended && draft.Advice("s3").Recommended);
    Check(draft.Put("s2") is null && draft.ActiveId == "l2" && draft.ActiveOriginSlotId == "s2");
    var advice = draft.Advice("s2"); Check(advice.Allowed && !advice.Recommended && advice.Displaced > 0 && advice.Message.Contains("밀려날 수업") && advice.Message.Contains("교사A"));
    var held = draft.Held; var display = draft.DisplayState; draft.Deselect();
    Check(draft.ActiveId == "l2" && draft.Held.SequenceEqual(held) && DocumentIntegrity.StateEquals(display,draft.DisplayState));
    Check(draft.Take("l2") is null && draft.ActiveOriginSlotId == "s2");
    Check(draft.Undo() && draft.ActiveId == "l1" && draft.ActiveOriginSlotId == "s1");
    Check(draft.Undo() && draft.Held.IsEmpty && draft.ActiveOriginSlotId is null && Json(session.Document) == before);
});
Test("R906", "저장 이름은 시각 한 번만 붙이고 같은 초의 저장도 기존 파일을 피함", () => {
    var time = new DateTime(2026,9,16,14,5,9);
    var stamped = TimestampedFileNames.Stamp(Path.Combine(temporary,"시간표.ttm"),time);
    Check(Path.GetFileName(stamped) == "시간표_20260916_140509.ttm" && Path.GetDirectoryName(stamped) == temporary);
    Check(TimestampedFileNames.Stamp(stamped,time) == stamped);
    Check(Path.GetFileName(TimestampedFileNames.Stamp(Path.Combine(temporary,"시간표_20260915_083000_02.ttm"),time)) == "시간표_20260916_140509.ttm");
    var second = TimestampedFileNames.Next(stamped,time,p => p == stamped);
    Check(second.EndsWith("_20260916_140509_02.ttm") && TimestampedFileNames.Next(stamped,time,p => p == stamped || p == second).EndsWith("_03.ttm"));
    Check(TimestampedFileNames.Stamp("결과.csv",time) == "결과_20260916_140509.csv");
});

// r10: 전체 교사 잠금과 배치·검증 결과 재사용의 무효화 회귀 소스. 미실행.
Test("R1001", "교사 전체 잠금은 숨겨진 다음 주를 포함하며 타 교사·기존 잠금을 보존", () => {
    var state = State() with { Lessons = State().Lessons.Select(x => x.Id == "l1" ? x with { Locked = true } : x)
        .Append(new LessonInstance("next-week","c1","수학","t1","s9")).ToImmutableArray() };
    var session = Session(state); var before = Json(session.Document);
    var plan = ChangePlanner.SetTeacherLocks(session,"t1",true);
    Check(plan.Changes.Length == 2 && plan.Changes.Any(x => x.Before.Id == "next-week") && Json(session.Document) == before);
    Check(session.Apply(plan).IsValid && session.Document.HistoryCursor == 1);
    Check(session.Document.Current.Lessons.Where(x => x.TeacherId == "t1").All(x => x.Locked));
    Check(session.Document.Current.Lessons.Where(x => x.TeacherId != "t1").All(x => !x.Locked));
    Check(ChangePlanner.SetTeacherLocks(session,"t1",true).Changes.IsEmpty && ChangeHistory.Operations(session.Document).Count == 0);
    Check(session.Undo() && DocumentIntegrity.StateEquals(state,session.Document.Current));
    Check(session.Redo() && session.Apply(ChangePlanner.SetTeacherLocks(session,"t1",false)).IsValid);
    Check(session.Document.Current.Lessons.All(x => !x.Locked));
});
Test("R1002", "수동 배치·카드 전환·Undo 후 추천과 밀려날 교사가 갱신되고 만료 초안을 거부", () => {
    var session = Session(); var before = Json(session.Document); var draft = new ManualChainDraft(session);
    Check(draft.Take("l1") is null && draft.Advice("s3").Recommended && draft.Advice("s2").Displaced == 2);
    Check(draft.Put("s2") is null && draft.ActiveId == "l2" && draft.Advice("s1").Recommended);
    Check(draft.Take("l3") is null);
    var advice = draft.Advice("s1");
    Check(advice.Allowed && !advice.Recommended && advice.Displaced == 1 && advice.Message.Contains("과학 · 교사C"));
    Check(draft.DisplacedAt("s1").Single().Id == "l4");
    draft.Deselect(); Check(draft.ActiveId == "l3" && draft.Advice("s1").Allowed && draft.Held.Length == 2);
    Check(draft.Take("l2") is null && draft.Advice("s1").Recommended);
    Check(draft.Undo() && draft.ActiveId == "l1" && draft.Held.Length == 1 && draft.PlacedChanges.IsEmpty);
    Check(!draft.Advice("s1").Recommended && draft.Advice("s2").Displaced == 2 && draft.Advice("s3").Recommended);
    Check(Json(session.Document) == before);
    Check(session.Apply(ChangePlanner.ToggleLock(session,"l4")).IsValid);
    Check(!draft.Advice("s3").Allowed && draft.Put("s3") is not null && draft.Plan() is null);
});
Test("R1003", "미리보기 재사용 중 잠금·날짜·행사 변경은 적용 가능 상태를 무효화", () => {
    foreach (Action<ProjectSession> mutate in new Action<ProjectSession>[] {
        s => Check(s.Apply(ChangePlanner.ToggleLock(s,"l1")).IsValid),
        s => s.SetCalendar(new(new(2026,9,14),new(2026,9,15))),
        s => s.SetGradeDateExclusions([new(new(2026,9,14),1)]) })
    {
        var session = Session(); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
        var vm = new MainViewModel(); vm.Attach(session); vm.ShowPlan(ChangePlanner.Move(session,"l1","s3"));
        Check(vm.CanApply && vm.CanApply); mutate(session); var after = Json(session.Document);
        Check(!vm.CanApply && !vm.Apply() && Json(session.Document) == after);
        var fresh = Session(); vm.Attach(fresh); vm.ShowPlan(ChangePlanner.Move(fresh,"l1","s3"));
        Check(vm.CanApply && vm.Apply() && fresh.Document.Current.Lessons.Single(x => x.Id == "l1").SlotId == "s3");
    }
});

// r11: 월 경계 저장·집계와 당일 우선/단계 확대 회귀 소스. Windows에서 실행, 여기서는 미실행.
ScheduleState MonthlyFixture() => new([new("a","교사A"),new("b","교사B"),new("c","교사C")], [new("c1","1-1")],
    Enumerable.Range(1,5).SelectMany(d => Enumerable.Range(1,3).Select(p => new TimeSlot($"d{d}p{p}",1,d,p))).ToImmutableArray(),
    Enumerable.Range(1,5).Select(d => new LessonInstance("m"+d,"c1","수학","a",$"d{d}p1")).ToImmutableArray(), []);
string DatedSlot(ProjectDocument doc, DateOnly date, int period) => doc.Current.Slots.Single(x => doc.Calendar!.DateOf(x) == date && x.Period == period).Id;
Test("R1101", "윤년·연말을 포함한 월 생성은 날짜 중복 없이 확장하고 이전 월 전환도 실제 날짜 유지", () => {
    foreach (var month in new[] { new DateOnly(2024,2,1), new DateOnly(2026,10,1), new DateOnly(2026,12,1) })
    {
        var doc = MonthlyTimetable.EnsureMonth(ProjectDocument.Create("월 시험",MonthlyFixture()),month);
        if (month == new DateOnly(2026,10,1)) Check(doc.Current.Slots.Any(x => doc.Calendar!.DateOf(x) == new DateOnly(2026,11,2)));
        var end = month.AddMonths(1).AddDays(-1);
        foreach (var date in Enumerable.Range(0,end.DayNumber-month.DayNumber+1).Select(i => month.AddDays(i)).Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)))
            Check(doc.Current.Slots.Count(x => doc.Calendar!.DateOf(x) == date) == 3);
        var dates = doc.Current.Slots.ToDictionary(x => x.Id,x => doc.Calendar!.DateOf(x));
        var previous = MonthlyTimetable.EnsureMonth(doc,month.AddMonths(-1));
        Check(previous.Current.Slots.Where(x => dates.ContainsKey(x.Id)).All(x => previous.Calendar!.DateOf(x) == dates[x.Id]));
        var next = MonthlyTimetable.EnsureMonth(previous,month.AddMonths(1));
        var again = MonthlyTimetable.EnsureMonth(next,month);
        Check(again.Current.Slots.GroupBy(x => (again.Calendar!.DateOf(x),x.Period)).All(x => x.Count() == 1));
        Check(again.Current.Lessons.Select(x => x.Id).Distinct().Count() == again.Current.Lessons.Length);
        Check(again.Monthly!.CoverageId == next.Monthly!.CoverageId && again.SchemaVersion == 3);
    }
});
Test("R1102", "월 경계 이동·보강은 저장·재열기와 Undo/Redo 후에도 수업·이력 보존", () => {
    var session = new ProjectSession(MonthlyTimetable.EnsureMonth(ProjectDocument.Create("월 시험",MonthlyFixture()),new(2026,9,1)));
    var from = DatedSlot(session.Document,new(2026,9,30),1); var to = DatedSlot(session.Document,new(2026,10,1),2);
    var id = session.Document.Current.Lessons.Single(x => x.SlotId == from).Id;
    Check(session.Apply(ChangePlanner.Move(session,id,to)).IsValid);
    Check(session.Apply(ChangePlanner.AssignTeacher(session,id,"b")).IsValid);
    session.SelectMonth(new(2026,10,1));
    Check(session.Document.HistoryCursor == 2 && session.Document.Current.Lessons.Single(x => x.Id == id) is { TeacherId: "b" } current && current.SlotId == to);
    var path = Path.Combine(temporary,"r11-month.ttm"); ProjectRepository.WriteAtomic(path,session.Document);
    var loaded = new ProjectSession(ProjectRepository.Read(path)); Check(Json(loaded.Document) == Json(session.Document));
    Check(loaded.Undo() && loaded.Document.Current.Lessons.Single(x => x.Id == id).TeacherId == "a");
    Check(loaded.Undo() && ChangeHistory.NetChanges(loaded.Document).IsEmpty);
    Check(loaded.Redo() && loaded.Redo());
    loaded.SelectMonth(new(2026,9,1)); Check(loaded.Document.Current.Lessons.Single(x => x.Id == id).SlotId == to);
});
Test("R1103", "월 왕복 확장·행사 설정의 저장 상태와 형식 버전 유지, 날짜 재해석 차단", () => {
    var session = new ProjectSession(MonthlyTimetable.EnsureMonth(ProjectDocument.Create("월 시험",MonthlyFixture()),new(2026,9,1)),true);
    Check(!session.IsDirty); var coverage = session.Document.Monthly!.CoverageId;
    session.SelectMonth(new(2026,10,1)); session.SelectMonth(new(2026,9,1));
    Check(session.IsDirty && session.Document.Monthly!.CoverageId != coverage);
    session.SetGradeDateExclusions([new(new(2026,9,22),1)]); Check(session.Document.SchemaVersion == 3);
    session.MarkSaved(session.Document); Check(!session.IsDirty);
    var before = Json(session.Document); var calendar = session.Document.Calendar!;
    Throws<InvalidOperationException>(() => session.SetCalendar(calendar with { FirstWeekMonday = calendar.FirstWeekMonday.AddDays(7) }));
    Check(Json(session.Document) == before);
    Throws<InvalidDataException>(() => DocumentIntegrity.EnsureValid(session.Document with { SchemaVersion = 2 }));
    var old = Session(); old.SetCalendar(new(new(2026,9,14),new(2026,9,14))); ApplyMove(old);
    var originalId = old.Document.Current.Lessons.Single(x => x.Id == "l1").SlotId;
    old.SelectMonth(new(2026,9,1)); Check(old.Document.Current.Lessons.Single(x => x.Id == "l1").SlotId == originalId && old.Document.HistoryCursor == 1);
    Check(old.Undo() && old.Document.Current.Lessons.Single(x => x.Id == "l1").SlotId == "s1");
});
Test("R1104", "당일 연쇄가 다음 날짜의 직접 이동보다 우선", () => {
    var state = new ScheduleState([new("a","A"),new("b","B"),new("c","C"),new("d","D")], [new("c1","1-1"),new("c2","1-2")],
        [new("from",1,1,1),new("to",1,1,2),new("spare",1,1,3),new("next",1,2,1)],
        [new("source","c1","수학","a","from"),new("other","c1","국어","b","to"),new("conflict","c2","수학","b","from"),
         new("fixed1","c2","과학","c","to",Locked:true),new("fixed2","c1","사회","d","spare",Locked:true)], []);
    var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    Check(session.Preview(ChangePlanner.Move(session,"source","next")).IsValid);
    var result = CalendarSwapSearch.Find(session,"source",null,state.Slots.Select(x => x.Id).ToHashSet(),SwapDirection.Both,CancellationToken.None);
    Check(result.Plan is not null,result.Message);
    var slots = state.Slots.ToDictionary(x => x.Id);
    Check(result.Plan!.Changes.Length >= 3 && result.Plan.Changes.All(x => slots[x.Before.SlotId].Day == 1 && slots[x.After.SlotId].Day == 1));
});
Test("R1105", "당일 해가 없으면 허용 방향의 가까운 날짜로 보조 수업 확장", () => {
    var state = new ScheduleState([new("a","A"),new("b","B"),new("c","C")], [new("c1","1-1"),new("c2","1-2")],
        [new("from",1,3,1),new("to",1,3,2),new("earlier",1,2,1),new("next-month",1,4,1)],
        [new("source","c1","수학","a","from"),new("other","c1","국어","b","to"),new("conflict","c2","수학","b","from"),new("fixed","c2","과학","c","to",Locked:true)], []);
    var session = Session(state); session.SetCalendar(new(new(2026,9,28),new(2026,9,28)));
    var scope = state.Slots.Select(x => x.Id).ToHashSet();
    Check(CalendarSwapSearch.Find(session,"source","to",scope,SwapDirection.SameDay,CancellationToken.None).Plan is null);
    var both = CalendarSwapSearch.Find(session,"source","to",scope,SwapDirection.Both,CancellationToken.None);
    Check(both.Plan is not null && both.Plan.Changes.Any(x => x.After.SlotId == "earlier") && both.Plan.Changes.All(x => x.After.SlotId != "next-month"),both.Message);
    var later = CalendarSwapSearch.Find(session,"source","to",scope,SwapDirection.Later,CancellationToken.None);
    Check(later.Plan is not null && later.Plan.Changes.Single(x => x.Before.Id == "source").After.SlotId == "to" && later.Plan.Changes.Any(x => x.After.SlotId == "next-month"),later.Message);
    Check(MonthlyTimetable.CrossesMonth(session.Document,later.Plan!));
});
Test("R1106", "담당 교체 원상복귀·잠금 유지, 다음 달의 빈 시간도 동일 조건으로 추천", () => {
    var session = new ProjectSession(MonthlyTimetable.EnsureMonth(ProjectDocument.Create("월 시험",MonthlyFixture()),new(2026,9,1)));
    var id = session.Document.Current.Lessons.Single(x => x.SlotId == DatedSlot(session.Document,new(2026,9,30),1)).Id;
    Check(session.Apply(ChangePlanner.AssignTeacher(session,id,"b")).IsValid && session.Apply(ChangePlanner.AssignTeacher(session,id,"c")).IsValid);
    Check(ChangeHistory.NetChanges(session.Document).Length == 1);
    Check(session.Apply(ChangePlanner.AssignTeacher(session,id,"a")).IsValid && session.Apply(ChangePlanner.ToggleLock(session,id)).IsValid);
    Check(ChangeHistory.NetChanges(session.Document).IsEmpty);
    Check(session.Apply(ChangePlanner.ToggleLock(session,id)).IsValid);
    var draft = new ManualChainDraft(session); Check(draft.Take(id) is null);
    Check(draft.Advice(DatedSlot(session.Document,new(2026,9,30),2)).Recommended);
    Check(draft.Advice(DatedSlot(session.Document,new(2026,10,1),2)).Recommended);
});

// r12: 조회 날짜·출력 자료형·대기 선택/참여 순서 회귀 소스. Windows에서 실행할 항목이며 여기서는 미실행.
Test("R1201", "기본 조회는 오늘부터 다음 주 금요일: 월 경계 포함·주말 제외", () => {
    var today = new DateOnly(2026,9,30);
    Check(BoardDateRange.NextFriday(today) == new DateOnly(2026,10,9));
    Check(!BoardDateRange.IsDefaultVisible(today.AddDays(-1),today));
    Check(BoardDateRange.IsDefaultVisible(today,today) && BoardDateRange.IsDefaultVisible(new(2026,10,9),today));
    Check(!BoardDateRange.IsDefaultVisible(new(2026,10,10),today));
    Check(!BoardDateRange.IsDefaultVisible(new(2026,10,3),today) && !BoardDateRange.IsDefaultVisible(new(2026,10,4),today));
    Check(BoardDateRange.NextFriday(new(2026,9,18)) == new DateOnly(2026,9,25));
    Check(BoardDateRange.NextFriday(new(2026,9,20)) == new DateOnly(2026,9,25));
});
Test("R1202", "CSV 학급 보호·재읽기, Excel 텍스트 셀, 외부 교사코드 정리", () => {
    var state = Rows("학급,요일,교시,과목,교사\n1-1,월,1,수학,교사A\n1-2,화,2,국어,교사B\n").Document!.Current;
    var csv = Path.Combine(temporary,"r12-results.csv"); CsvExporter.Write(csv,state);
    var table = TabularReader.Read(csv)[0];
    Check(table.Cell(1,0) == "=\"1-1\"" && table.Cell(1,6) == "교사A");
    var restored = rowsImporter.Parse(table,LongTableImporter.Suggest(table));
    Check(restored.CanImport && restored.Document!.Current.Classes.Select(x => x.Name).Order().SequenceEqual(new[] { "1-1","1-2" }));
    Check(state.Teachers.All(x => x.Id.StartsWith("name:",StringComparison.Ordinal)));
    Check(CsvExporter.EscapeClass("=1+1") == CsvExporter.Escape("=1+1"));
    Check(CsvExporter.EscapeClass("1-1\"&NOW()") == CsvExporter.Escape("1-1\"&NOW()"));
    Check(CsvExporter.ReadClass("=SUM(1,2)") == "=SUM(1,2)");
    var xlsx = Path.Combine(temporary,"r12-results.xlsx");
    CsvExporter.Write(xlsx,state with { Lessons = state.Lessons.Select(x => x with { Subject = "=1+1" }).ToImmutableArray() });
    using var book = new XLWorkbook(xlsx); var sheet = book.Worksheet(1);
    Check(sheet.Cell(2,1).DataType == XLDataType.Text && sheet.Cell(2,1).GetString() == "1-1" && !sheet.Cell(2,1).HasFormula);
    Check(sheet.Cell(2,5).GetString() == "=1+1" && !sheet.Cell(2,5).HasFormula && sheet.Cell(2,7).GetString() == "교사A");
});

Test("R1204", "대기 카드 재클릭으로 배치가 끊기지 않고 참여 교사 순서를 중복 없이 유지", () => {
    var session = Session(); var before = Json(session.Document); var draft = new ManualChainDraft(session);
    Check(draft.Take("l1") is null); draft.Deselect(); Check(draft.ActiveId == "l1");
    Check(draft.Put("s2") is null && draft.ActiveId == "l2");
    Check(draft.ParticipatingTeachers.SequenceEqual(new[] { "t1","t2" }));
    draft.Deselect(); Check(draft.ActiveId == "l2" && draft.Put("s3") is null);
    Check(draft.ActiveId == "l3" && draft.Put("s1") is null && draft.ActiveId == "l4");
    Check(draft.ParticipatingTeachers.SequenceEqual(new[] { "t1","t2","t3" }));
    draft.Deselect(); Check(draft.Put("s4") is null && draft.Held.IsEmpty);
    var plan = draft.Plan(); Check(plan is not null && session.Preview(plan).IsValid && Json(session.Document) == before);
    Check(draft.Undo() && draft.ActiveId == "l4" && draft.ParticipatingTeachers.SequenceEqual(new[] { "t1","t2","t3" }));
    draft.Deselect(); Check(draft.ActiveId == "l4" && draft.Put("s4") is null);
    Check(session.Apply(draft.Plan()!).IsValid && session.Document.HistoryCursor == 1);
});

// v4 alpha2: 재변경·수동 날짜 범위·하루 일괄 이동. Windows 실행용 회귀 소스이며 작성 환경에서는 미실행.
Test("V4A201", "재변경 확인은 실제 이동·보강만 포함하고 승인·Undo·다음 작업을 구분", () => {
    var session = Session(State() with { Lessons = State().Lessons.Where(x => x.ClassId == "c1").ToImmutableArray() });
    var review = new RechangeReview();
    Check(session.Apply(ChangePlanner.ToggleLock(session,"l1")).IsValid);
    Check(review.Required(session,["l1","l2"]).Length == 0);
    Check(session.Undo()); ApplyMove(session,"l1","s2");
    Check(review.Required(session,["l1","l2","l2"]).ToHashSet().SetEquals(new[] {"l1","l2"}));
    review.Approve(session,["l1"]);
    Check(review.Required(session,["l1","l2"]).SequenceEqual(new[] {"l2"}));
    review.Reset(); Check(review.Required(session,["l1"]).Length == 1);
    Check(session.Undo()); Check(review.Required(session,["l1","l2"]).Length == 0);
    Check(session.Redo()); review.Approve(session,["l1","l2"]);
    ApplyMove(session,"l1","s1"); // Explicitly move back: applied history still requires a new decision.
    Check(ChangeHistory.NetChanges(session.Document).IsEmpty && review.Required(session,["l1","l2"]).Length == 2);
    Check(session.Apply(ChangePlanner.AssignTeacher(session,"l2","t4")).IsValid);
    Check(review.Required(session,["l2"]).Length == 1);
});
Test("V4A202", "수동 당일 제한·날짜 허용 변경·사용 중 범위 축소 거부·Undo 기준일 복원", () => {
    var session = Session(); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var before = Json(session.Document); var draft = new ManualChainDraft(session,SwapDirection.SameDay);
    Check(draft.Take("l1") is null && draft.DateAnchorSlotId == "s1");
    Check(!draft.Advice("s9").Allowed && draft.AdviceBeforeDatePermission("s9").Allowed);
    Check(draft.Put("s9") is not null && draft.Held.Single().Id == "l1");
    Check(draft.SetDirection(SwapDirection.Later) is null && draft.Advice("s9").Allowed && draft.Put("s9") is null);
    Check(draft.SetDirection(SwapDirection.SameDay) is not null && draft.Direction == SwapDirection.Later);
    Check(draft.Plan() is { } plan && session.Preview(plan).IsValid && Json(session.Document) == before);
    Check(draft.Undo() && draft.SetDirection(SwapDirection.SameDay) is null);
    Check(draft.Undo() && draft.DateAnchorSlotId is null && draft.Held.IsEmpty);
});
Test("V4A203", "수동 날짜 허용으로도 기준일·잠금·행사 제외를 우회할 수 없음", () => {
    var source = State() with { Lessons = [new("late","c1","수학","t1","s9")] };
    var session = Session(source); session.SetCalendar(new(new(2026,9,14),new(2026,9,15)));
    var draft = new ManualChainDraft(session,SwapDirection.Both); Check(draft.Take("late") is null);
    Check(!draft.AdviceBeforeDatePermission("s1").Allowed && draft.Put("s1") is not null);
    session = Session(); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    Check(session.Apply(ChangePlanner.ToggleLock(session,"l2")).IsValid);
    draft = new ManualChainDraft(session,SwapDirection.Both); Check(draft.Take("l1") is null);
    Check(!draft.AdviceBeforeDatePermission("s2").Allowed && draft.Put("s2") is not null);
    session = Session(); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    session.SetGradeDateExclusions([new(new(2026,9,23),1)]);
    draft = new ManualChainDraft(session,SwapDirection.Both); Check(draft.Take("l1") is null);
    Check(!draft.AdviceBeforeDatePermission("s9").Allowed);
});
Test("V4A204", "고정 수업은 자동 연쇄의 직접 맞교환과 충돌 해소 모두에서 유지", () => {
    var pair = Session(State() with { Lessons = State().Lessons.Where(x => x.ClassId == "c1").ToImmutableArray() });
    var scope = pair.Document.Current.Slots.Select(x => x.Id).ToHashSet(); var before = Json(pair.Document);
    Check(SwapSearch.Find(pair,"l1","s2",scope,CancellationToken.None,immovable:new HashSet<string>{"l2"}).Plan is null);
    Check(Json(pair.Document) == before);
    var session = Session(); before = Json(session.Document);
    var result = SwapSearch.Find(session,"l1","s2",scope,CancellationToken.None,immovable:new HashSet<string>{"l3"});
    Check(result.Plan is null && Json(session.Document) == before); // Fixed l1 target would collide with pinned l3.
});
ScheduleState TeacherDayFixture(bool startMorning = false) => new(State().Teachers,State().Classes,
    Enumerable.Range(1,5).SelectMany(d => Enumerable.Range(1,8).Select(p => new TimeSlot($"d{d}p{p}",1,d,p))).ToImmutableArray(),
    Enumerable.Range(1,3).Select(p => new LessonInstance("day"+p,"c1","수학","t1",$"d3p{(startMorning ? p : p + 4)}"))
        .Concat(Enumerable.Range(1,3).Select(p => new LessonInstance("other"+p,"c1","국어","t2",$"d3p{(startMorning ? p + 4 : p)}")))
        .Concat([new LessonInstance("monday","c1","수학","t1","d1p1"),new LessonInstance("friday","c1","수학","t1","d5p1")]).ToImmutableArray(), []);
Test("V4A205", "하루 오전·오후 이동의 명시적 연강 허용, 원자 적용·한 번 Undo", () => {
    foreach (var goal in new[] { TeacherDayGoal.Morning,TeacherDayGoal.Afternoon })
    {
        var state = TeacherDayFixture(goal == TeacherDayGoal.Afternoon);
        var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
        var before = Json(session.Document); var revision = session.Revision;
        var boundary = goal == TeacherDayGoal.Morning ? 3 : 5; // Exactly three target periods forces 3 consecutive.
        var result = TeacherDaySearch.Find(session,new("t1",new(2026,9,16),goal,boundary),state.Slots.Select(x => x.Id).ToHashSet(),CancellationToken.None,allowConsecutive:true);
        Check(result.Plan is not null,result.Message);
        Check(Json(session.Document) == before && session.Revision == revision && session.Preview(result.Plan!).IsValid);
        Check(session.Apply(result.Plan!).IsValid && session.Document.HistoryCursor == 1);
        var slots = state.Slots.ToDictionary(x => x.Id);
        var periods = session.Document.Current.Lessons.Where(x => x.Id.StartsWith("day",StringComparison.Ordinal)).Select(x => slots[x.SlotId].Period).Order().ToArray();
        Check(periods.SequenceEqual(goal == TeacherDayGoal.Morning ? new[] {1,2,3} : new[] {6,7,8}));
        Check(session.Document.Current.Lessons.Where(x => x.Id is "monday" or "friday").All(x => x == state.Lessons.Single(b => b.Id == x.Id)));
        Check(session.Undo() && DocumentIntegrity.StateEquals(state,session.Document.Current) && session.Redo());
    }
});
Test("V4A206", "하루 전체 배치 불가·잠금·행사 제외·취소는 일부 이동도 남기지 않음", () => {
    var state = TeacherDayFixture(); var scope = state.Slots.Select(x => x.Id).ToHashSet();
    var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var request = new TeacherDayRequest("t1",new(2026,9,16),TeacherDayGoal.Morning,2,SpillOverflow:true);
    var before = Json(session.Document);
    Check(TeacherDaySearch.Find(session,request,scope,CancellationToken.None).Plan is null && Json(session.Document) == before);
    Check(session.Apply(ChangePlanner.ToggleLock(session,"day1")).IsValid); before = Json(session.Document);
    Check(TeacherDaySearch.Find(session,request with {MorningLastPeriod=4},scope,CancellationToken.None).Plan is null && Json(session.Document) == before);
    Check(session.Undo()); session.SetGradeDateExclusions([new(new(2026,9,16),1)]); before = Json(session.Document);
    Check(TeacherDaySearch.Find(session,request with {MorningLastPeriod=4},scope,CancellationToken.None).Plan is null && Json(session.Document) == before);
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    Throws<OperationCanceledException>(() => TeacherDaySearch.Find(session,request,scope,cancel.Token));
    Check(Json(session.Document) == before);
});
Test("V4A207", "하루 비우기는 이전·이후·양방향과 기존 다른 날 수업을 유지", () => {
    foreach (var direction in new[] { SwapDirection.Earlier,SwapDirection.Later,SwapDirection.Both })
    {
        var state = TeacherDayFixture(); var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
        var scope = state.Slots.Select(x => x.Id).ToHashSet(); var before = Json(session.Document);
        var request = new TeacherDayRequest("t1",new(2026,9,16),TeacherDayGoal.OtherDates,Direction:direction);
        var result = TeacherDaySearch.Find(session,request,scope,CancellationToken.None);
        Check(result.Plan is not null,result.Message); Check(Json(session.Document) == before);
        var slots = state.Slots.ToDictionary(x => x.Id); var calendar = session.Document.Calendar!;
        Check(result.Plan!.Changes.All(x => CalendarSwapSearch.Allows(calendar,slots["d3p5"],slots[x.Before.SlotId],direction) && CalendarSwapSearch.Allows(calendar,slots["d3p5"],slots[x.After.SlotId],direction)));
        Check(session.Apply(result.Plan!).IsValid);
        Check(!session.Document.Current.Lessons.Any(x => x.TeacherId == "t1" && slots[x.SlotId].Day == 3));
        Check(session.Document.Current.Lessons.Where(x => x.Id is "monday" or "friday").All(x => x == state.Lessons.Single(b => b.Id == x.Id)));
        Check(session.Document.HistoryCursor == 1 && session.Undo() && DocumentIntegrity.StateEquals(state,session.Document.Current));
    }
});
Test("V4A208", "월말 하루 이동은 다음 달로 이동 후 한 작업으로 복원", () => {
    var session = new ProjectSession(MonthlyTimetable.EnsureMonth(ProjectDocument.Create("월말 하루 이동",MonthlyFixture()),new(2026,9,1)));
    var before = Json(session.Document); var from = DatedSlot(session.Document,new(2026,9,30),1);
    var id = session.Document.Current.Lessons.Single(x => x.SlotId == from).Id;
    var result = TeacherDaySearch.Find(session,new("a",new(2026,9,30),TeacherDayGoal.OtherDates,Direction:SwapDirection.Later),
        MonthlyTimetable.WorkingSlots(session.Document).Select(x => x.Id).ToHashSet(),CancellationToken.None);
    Check(result.Plan is not null,result.Message); Check(Json(session.Document) == before && MonthlyTimetable.CrossesMonth(session.Document,result.Plan!));
    Check(session.Apply(result.Plan!).IsValid && session.Document.HistoryCursor == 1);
    var after = session.Document.Current.Lessons.Single(x => x.Id == id);
    Check(session.Document.Calendar!.DateOf(session.Document.Current.Slots.Single(x => x.Id == after.SlotId)).Month == 10);
    Check(session.Undo() && ChangeHistory.NetChanges(session.Document).IsEmpty);
});

// Own-lesson moves are permitted in proposals; explicit refusals pin the corresponding helpers.
ScheduleState SelfDisplacementState() => State() with {
    Lessons = [new("source","c1","음악A","t1","s2"),new("own","c1","음악B","t1","s6"),new("other","c1","기술","t2","s7")],
    Blocks = [new("t2","s2")]
};
Test("V4A401", "본인 추가 수업 이동을 거부하면 기존 수업을 고정하고 출발 시간을 비움", () => {
    var state = SelfDisplacementState(); var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var scope = state.Slots.Select(x => x.Id).ToHashSet(); var before = Json(session.Document);
    var result = CalendarSwapSearch.Find(session,"source","s7",scope,SwapDirection.SameDay,CancellationToken.None,immovable:new HashSet<string>{"own"});
    Check(result.Plan is not null,result.Message); Check(Json(session.Document) == before);
    Check(result.Plan!.Changes.All(x => x.Before.Id != "own"));
    Check(session.Apply(result.Plan!).IsValid);
    Check(session.Document.Current.Lessons.Single(x => x.Id == "source").SlotId == "s7");
    Check(session.Document.Current.Lessons.Single(x => x.Id == "own").SlotId == "s6");
    Check(!session.Document.Current.Lessons.Any(x => x.TeacherId == "t1" && x.SlotId == "s2"));
});
Test("V4A402", "본인 수업이 있는 목적도 교체안에 포함하며 거부 시 고정", () => {
    var state = SelfDisplacementState();
    state = state with { Lessons = state.Lessons.Concat(new[] {1,3,4,5,8}.Select(p => new LessonInstance("fixed"+p,"c1","사회","t3","s"+p,Locked:true))).ToImmutableArray() };
    var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14))); var before = Json(session.Document);
    var scope = state.Slots.Select(x => x.Id).ToHashSet();
    var own = CalendarSwapSearch.Find(session,"source","s6",scope,SwapDirection.Both,CancellationToken.None);
    Check(own.Plan is not null && session.Preview(own.Plan).IsValid);
    Check(own.Plan!.Changes.Any(x => x.Before.Id == "own"));
    Check(CalendarSwapSearch.Find(session,"source","s6",scope,SwapDirection.Both,CancellationToken.None,immovable:new HashSet<string>{"own"}).Plan is null);
    Check(Json(session.Document) == before);
});
ScheduleState ConsecutiveFixture() => State() with { Lessons = [
    new("source","c1","수학","t1","s1"), new("own3","c1","수학","t1","s3"), new("own4","c1","수학","t1","s4") ] };
Test("V4A501", "AI는 가까운 3연강 목적보다 연강 없는 목적 우선, 고정 목적은 허용 전 반환 안 함", () => {
    var session = Session(ConsecutiveFixture()); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var scope = session.Document.Current.Slots.Select(x => x.Id).ToHashSet(); var before = Json(session.Document);
    var result = CalendarSwapSearch.Find(session,"source",null,scope,SwapDirection.SameDay,CancellationToken.None,immovable:new HashSet<string>{"own3","own4"});
    Check(result.Plan is not null && new ConsecutiveTeaching(session.Document.Current).Evaluate(result.Plan).Count == 0);
    Check(result.Plan!.Changes.Single(x => x.Before.Id == "source").After.SlotId == "s6");
    Check(result.Plan!.Changes.All(x => x.Before.Id == "source"));
    Check(CalendarSwapSearch.Find(session,"source","s2",scope,SwapDirection.SameDay,CancellationToken.None).Plan is null);
    var allowed = CalendarSwapSearch.Find(session,"source","s2",scope,SwapDirection.SameDay,CancellationToken.None,allowConsecutive:true,immovable:new HashSet<string>{"own3","own4"});
    Check(allowed.Plan is not null && allowed.Plan.Changes.Single(x => x.Before.Id == "source").After.SlotId == "s2");
    var risk = new ConsecutiveTeaching(session.Document.Current).Evaluate(allowed.Plan!).Single();
    Check(risk.TeacherId == "t1" && risk.FirstPeriod == 2 && risk.LastPeriod == 4 && risk.Length == 3);
    Check(Json(session.Document) == before); // Finding or discarding either preview never applies it.
});
Test("V4A502", "AI는 밀려난 교사의 3연강도 검토하고 연쇄로 회피", () => {
    var state = State() with { Lessons = [new("source","c1","수학","t1","s1"), new("other","c1","국어","t2","s6"),
        new("b2","c2","국어","t2","s2"),new("b3","c2","국어","t2","s3")] };
    var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var before = Json(session.Document); var review = new ConsecutiveTeaching(state);
    var pair = ChangePlanner.Move(session,"source","s6");
    Check(session.Preview(pair).IsValid && review.Evaluate(pair).Single().TeacherId == "t2");
    var result = CalendarSwapSearch.Find(session,"source","s6",state.Slots.Select(x => x.Id).ToHashSet(),SwapDirection.SameDay,CancellationToken.None);
    Check(result.Plan is not null && review.Evaluate(result.Plan).Count == 0 && session.Preview(result.Plan!).IsValid);
    Check(result.Plan!.Changes.Single(x => x.Before.Id == "source").After.SlotId == "s6");
    var narrow = new HashSet<string>{"s1","s6"};
    Check(CalendarSwapSearch.Find(session,"source","s6",narrow,SwapDirection.SameDay,CancellationToken.None).Plan is null);
    var fallback = CalendarSwapSearch.Find(session,"source","s6",narrow,SwapDirection.SameDay,CancellationToken.None,allowConsecutive:true);
    Check(fallback.Plan is not null && review.Evaluate(fallback.Plan).Single().TeacherId == "t2");
    Check(Json(session.Document) == before);
});
Test("V4A503", "기존 연강은 유지 가능, 새 연강·4연강 증가와 최대값에 가려진 다른 연강은 감지", () => {
    Check(ConsecutiveTeaching.NewRuns([1,2,3,6],[1,2,3,7]).Count == 0);
    Check(ConsecutiveTeaching.NewRuns([1,2,3,7],[1,2,3,4]).Single() == (1,4));
    Check(ConsecutiveTeaching.NewRuns([1,2,3,4],[1,2,3,7]).Count == 0);
    Check(ConsecutiveTeaching.NewRuns([1,2,3],[2,3,4]).Single() == (2,4));
    Check(ConsecutiveTeaching.NewRuns([1,2,3,4,6,7],[1,2,3,4,6,7,8]).Single() == (6,8));
    var state = State() with { Lessons = Enumerable.Range(1,3).Select(p => new LessonInstance("old"+p,"c1","수학","t1","s"+p))
        .Append(new("source","c2","국어","t2","s8")).ToImmutableArray() };
    var session = Session(state); var review = new ConsecutiveTeaching(state);
    Check(review.Evaluate(ChangePlanner.Move(session,"source","s7")).Count == 0);
    Check(review.Evaluate(ChangePlanner.ToggleLock(session,"old1")).Count == 0);
});
Test("V4A504", "연강은 교사별 실제 주·요일로 분리하고 2연강은 허용", () => {
    var state = State() with { Slots = State().Slots.AddRange(new[]{new TimeSlot("w2m1",2,1,1),new TimeSlot("w2m3",2,1,3)}),
        Lessons = [new("source","c1","수학","t1","w2m1"),new("old2","c1","수학","t1","s2"),new("old3","c1","수학","t1","s3")] };
    var session = Session(state); var review = new ConsecutiveTeaching(state);
    Check(review.Evaluate(ChangePlanner.Move(session,"source","w2m3")).Count == 0);
    var risk = review.Evaluate(ChangePlanner.Move(session,"source","s1")).Single();
    Check(risk.Day.Week == 1 && risk.Day.Day == 1 && risk.Length == 3);
    Check(ConsecutiveTeaching.NewRuns([1],[1,2]).Count == 0);
});
Test("V4A505", "수동 3연강은 추천 제외·배치 전 경고 자료 제공, 취소·Undo와 원본 유지", () => {
    var session = Session(ConsecutiveFixture()); var before = Json(session.Document);
    var draft = new ManualChainDraft(session); Check(draft.Take("source") is null);
    Check(draft.Advice("s2").Allowed && draft.Advice("s2").ThreeConsecutive && !draft.Advice("s2").Recommended);
    Check(draft.PlacementRisks("s2").Single().Length == 3 && draft.ActiveId == "source");
    Check(draft.PlacementRisks("s6").Count == 0 && draft.Advice("s6").Recommended);
    Check(draft.Held.Length == 1 && Json(session.Document) == before);
    Check(draft.Put("s2") is null && new ConsecutiveTeaching(session.Document.Current).Evaluate(draft.Plan()!).Count == 1);
    Check(draft.Undo() && draft.ActiveId == "source" && draft.Held.Length == 1 && Json(session.Document) == before);
    Check(draft.PlacementRisks("s1").Count == 0);
    var blocked = Session(ConsecutiveFixture() with { Blocks = [new("t1","s2")] });
    draft = new ManualChainDraft(blocked); Check(draft.Take("source") is null);
    Check(!draft.Advice("s2").Allowed && draft.Put("s2") is not null);
});
Test("V4A506", "보강·특별 보강의 새 3연강 검사와 후보 별표", () => {
    foreach (var teacher in new[]{"t2",SpecialCover.Principal})
    {
        var state = State() with { Teachers = State().Teachers.Add(new(SpecialCover.Principal,"교장")), Lessons = [
            new("source","c1","수학","t1","s3"), new("b1","c2","국어",teacher,"s1"),new("b2","c2","국어",teacher,"s2")] };
        var session = Session(state); var before = Json(session.Document);
        var candidate = TeachingAdvice.CoverCandidates(session,"source").Single(x => x.TeacherId == teacher);
        Check(candidate.ThreeConsecutive && candidate.Label.StartsWith("* "));
        var risks = new ConsecutiveTeaching(state).Evaluate(ChangePlanner.AssignTeacher(session,"source",teacher));
        Check(risks.Single().TeacherId == teacher && risks.Single().Length == 3 && Json(session.Document) == before);
    }
});
Test("V4A507", "오전·오후 몰기는 대상 교사 연강 기본 허용, 관련 교사 연강은 계속 검사", () => {
    var state = TeacherDayFixture() with { Lessons = TeacherDayFixture().Lessons.Where(x => !x.Id.StartsWith("other")).ToImmutableArray() };
    var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var request = new TeacherDayRequest("t1",new(2026,9,16),TeacherDayGoal.Morning);
    var scope = state.Slots.Select(x => x.Id).ToHashSet(); var before = Json(session.Document);
    var result = TeacherDaySearch.Find(session,request,scope,CancellationToken.None);
    Check(result.Plan is not null,result.Message);
    Check(new ConsecutiveTeaching(state).Evaluate(result.Plan!).Single().TeacherId == "t1");
    Check(result.Plan!.Changes.Where(x => x.Before.TeacherId == "t1").Select(x => state.Slots.Single(s => s.Id == x.After.SlotId).Period).Order().SequenceEqual(new[]{1,2,3}));
    Check(Json(session.Document) == before && session.Apply(result.Plan!).IsValid);
    Check(session.Undo() && DocumentIntegrity.StateEquals(state,session.Document.Current));
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    Throws<OperationCanceledException>(() => TeacherDaySearch.Find(session,request,scope,cancel.Token));
});
Test("V4A508", "연강 허용도 고정 목적·날짜·잠금·교사 배정 제외는 우회 못함", () => {
    foreach (var state in new[] { ConsecutiveFixture() with { Blocks = [new("t1","s2")] },
        ConsecutiveFixture() with { Lessons = ConsecutiveFixture().Lessons.Select(x => x.Id == "source" ? x with {Locked=true} : x).ToImmutableArray() } })
    {
        var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
        var before = Json(session.Document); var scope = state.Slots.Select(x => x.Id).ToHashSet();
        Check(CalendarSwapSearch.Find(session,"source","s2",scope,SwapDirection.SameDay,CancellationToken.None,allowConsecutive:true).Plan is null);
        Check(CalendarSwapSearch.Find(session,"source","s9",scope,SwapDirection.SameDay,CancellationToken.None,allowConsecutive:true).Plan is null);
        Check(Json(session.Document) == before);
    }
});

// Alpha6: weekday templates, saved ledger integrity, nearby placement and lesson-date anchored directions.
// Regression SOURCE only. Compile and run on Windows; no execution has been claimed here.
ScheduleState TemplateFixture() => State() with {
    Slots = Enumerable.Range(1,5).SelectMany(d => Enumerable.Range(1,d == 4 ? 7 : 6)
        .Select(p => new TimeSlot($"d{d}p{p}",1,d,p))).ToImmutableArray(),
    Lessons = Enumerable.Range(1,5).SelectMany(d => Enumerable.Range(1,d == 4 ? 7 : 6)
        .Select(p => new LessonInstance($"lesson{d}-{p}","c1",$"{d}요일-{p}",$"t{p % 4 + 1}",$"d{d}p{p}",Source:$"original-{d}-{p}"))).ToImmutableArray(), Blocks = []
};
ProjectSession TemplateSession(bool monthly = false)
{
    var document = ProjectDocument.Create("기초 요일 시험",TemplateFixture()) with { Calendar = new(new(2026,9,14),new(2026,9,14)) };
    return new(monthly ? MonthlyTimetable.EnsureMonth(document,new(2026,9,1)) : document,true);
}
Test("V4A606", "오전은 1·2·3, 오후는 4·5·6, 초과 옵션은 5·6 유지와 가까운 날 이동", () => {
    var state = State() with { Slots = Enumerable.Range(1,5).SelectMany(d => Enumerable.Range(1,6).Select(p => new TimeSlot($"d{d}p{p}",1,d,p))).ToImmutableArray(),
        Lessons = Enumerable.Range(1,3).Select(p => new LessonInstance("root"+p,"c1","수학","t1",$"d3p{p+3}")).ToImmutableArray(), Blocks = [] };
    foreach (var goal in new[]{TeacherDayGoal.Morning,TeacherDayGoal.Afternoon})
    {
        var start = goal == TeacherDayGoal.Morning ? state : state with { Lessons = state.Lessons.Select((x,i) => x with {SlotId=$"d3p{i+1}"}).ToImmutableArray() };
        var session = Session(start); session.SetCalendar(new(new(2026,9,14),new(2026,9,14))); var before = Json(session.Document);
        var result = TeacherDaySearch.Find(session,new("t1",new(2026,9,16),goal),start.Slots.Select(x => x.Id).ToHashSet(),CancellationToken.None);
        Check(result.Plan is not null,result.Message); Check(Json(session.Document) == before && session.Apply(result.Plan!).IsValid);
        Check(session.Document.Current.Lessons.Select(x => start.Slots.Single(s => s.Id == x.SlotId).Period).Order()
            .SequenceEqual(goal == TeacherDayGoal.Morning ? new[]{1,2,3} : new[]{4,5,6}));
    }
    var spillState = state with { Lessons = state.Lessons.Select((x,i) => x with {SlotId=$"d3p{i+1}"}).ToImmutableArray() };
    var spillSession = Session(spillState); spillSession.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var spill = TeacherDaySearch.Find(spillSession,new("t1",new(2026,9,16),TeacherDayGoal.Afternoon,4,SwapDirection.Later,SpillOverflow:true),spillState.Slots.Select(x => x.Id).ToHashSet(),CancellationToken.None);
    Check(spill.Plan is not null,spill.Message); Check(spillSession.Apply(spill.Plan!).IsValid);
    var positions = spillSession.Document.Current.Lessons.Select(x => spillState.Slots.Single(s => s.Id == x.SlotId)).ToArray();
    Check(positions.Where(x => x.Day == 3).Select(x => x.Period).Order().SequenceEqual(new[]{5,6}) && positions.Count(x => x.Day == 4) == 1);
});
Test("V4A607", "하루 비우기는 가까운 두 날짜 분산 우선, 관련 수업·기존 본인 수업 불필요 이동 없음", () => {
    var state = State() with { Slots = Enumerable.Range(1,5).SelectMany(d => Enumerable.Range(1,6).Select(p => new TimeSlot($"d{d}p{p}",1,d,p))).ToImmutableArray(),
        Lessons = new[]{1,3,5}.Select(p => new LessonInstance("root"+p,"c1","수학","t1",$"d3p{p}"))
            .Concat([new LessonInstance("other","c1","국어","t2","d4p2"),new LessonInstance("ownFriday","c2","수학","t1","d5p2")]).ToImmutableArray(), Blocks = [] };
    var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14))); var before = Json(session.Document);
    var result = TeacherDaySearch.Find(session,new("t1",new(2026,9,16),TeacherDayGoal.OtherDates,Direction:SwapDirection.Later),state.Slots.Select(x => x.Id).ToHashSet(),CancellationToken.None);
    Check(result.Plan is not null,result.Message); Check(Json(session.Document) == before);
    Check(result.Plan!.Changes.Length == 3 && result.Plan!.Changes.All(x => x.Before.Id.StartsWith("root")) && result.Plan!.Changes.Select(x => state.Slots.Single(s => s.Id == x.After.SlotId).Day).ToHashSet().SetEquals(new[]{4,5}));
});
Test("V4A608", "오늘 월요일·선택 수요일: 이전은 화요일 보조, 이후는 목요일 보조, 당일은 범위 확장 안 함", () => {
    var state = State() with { Slots = Enumerable.Range(2,3).SelectMany(d => Enumerable.Range(1,6).Select(p => new TimeSlot($"d{d}p{p}",1,d,p))).ToImmutableArray(),
        Lessons = new[]{new LessonInstance("source","c1","수학","t1","d3p6"),new LessonInstance("helper","c1","국어","t2","d3p1")}
            .Concat(Enumerable.Range(2,4).Select(p => new LessonInstance("locked"+p,"c1","사회","t3",$"d3p{p}",Locked:true))).ToImmutableArray(),
        Blocks = [new("t2","d3p6")] };
    var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14))); var before = Json(session.Document);
    var scope = state.Slots.Select(x => x.Id).ToHashSet();
    Check(CalendarSwapSearch.Find(session,"source","d3p1",scope,SwapDirection.SameDay,CancellationToken.None).Plan is null);
    foreach (var direction in new[]{SwapDirection.Earlier,SwapDirection.Later})
    {
        var result = CalendarSwapSearch.Find(session,"source","d3p1",scope,direction,CancellationToken.None);
        Check(result.Plan is not null,result.Message);
        Check(result.Plan!.Changes.Single(x => x.Before.Id == "source").After.SlotId == "d3p1");
        Check(state.Slots.Single(s => s.Id == result.Plan!.Changes.Single(x => x.Before.Id == "helper").After.SlotId).Day == (direction == SwapDirection.Earlier ? 2 : 4));
    }
    Check(Json(session.Document) == before);
});
Test("V4A609", "교시 제목 학년 다중 잠금은 선택 학년만 한 계획으로 적용·Undo, 잠금은 변경 내역 제외", () => {
    var state = State() with { Classes = [new("c1","1-1"),new("c2","2-1"),new("c3","3-1")],
        Lessons = [new("a","c1","수학","t1","s1"),new("b","c2","국어","t2","s1"),new("c","c3","사회","t3","s1")] };
    var session = Session(state); var ids = HeaderLockScope.LessonIds(state,"s1",new HashSet<int>{1,3});
    Check(ids.ToHashSet().SetEquals(new[]{"a","c"})); Check(session.Apply(ChangePlanner.SetLocks(session,ids,true)).IsValid);
    Check(session.Document.Current.Lessons.Single(x => x.Id == "b").Locked == false && session.Document.Current.Lessons.Count(x => x.Locked) == 2);
    Check(ChangeHistory.NetChanges(session.Document).IsEmpty && ChangeHistory.Operations(session.Document).Count == 0);
    Check(session.Undo() && DocumentIntegrity.StateEquals(state,session.Document.Current));
});

// Alpha7 regression source. Not compiled or executed in the source preparation environment.
Test("V4A701", "학교 계획은 기초 배정으로 반영, 변동 색상·이력·재변경에서 제외, 다른 날짜 Undo 유지", () => {
    var session = TemplateSession();
    Check(session.Apply(ChangePlanner.AssignTeacher(session,"lesson4-1","t3")).IsValid);
    var before = session.Document.Current; var json = Json(session.Document);
    var plan = DayTemplatePlanner.Create(session,new(2026,9,14),4).Plan!;
    Check(Json(session.Document) == json && session.Apply(plan).IsValid);
    Check(session.Document.SchemaVersion == 5 && session.Document.History.Length == 1);
    var slots = session.Document.Current.Slots.ToDictionary(x => x.Id);
    var date = new DateOnly(2026,9,14); var calendar = session.Document.Calendar!;
    var lessons = session.Document.Current.Lessons.Where(x => calendar.DateOf(slots[x.SlotId]) == date).ToArray();
    Check(lessons.Length == 7 && lessons.All(x => x.Subject.StartsWith("4요일")));
    Check(lessons.Single(x => slots[x.SlotId].Period == 1).TeacherId == "t2");
    Check(DayTemplatePlanner.OriginalPattern(session.Document).Lessons.SequenceEqual(TemplateFixture().Lessons));
    Check(ChangeHistory.NetChanges(session.Document).All(x => calendar.DateOf(slots[x.After.SlotId]) != date));
    Check(new RechangeReview().Required(session,lessons.Select(x => x.Id)).IsEmpty);
    Check(session.Document.Current.Lessons.Where(x => calendar.DateOf(slots[x.SlotId]) != date).OrderBy(x => x.Id)
        .SequenceEqual(before.Lessons.Where(x => !x.SlotId.StartsWith("d1")).OrderBy(x => x.Id)));
    Check(session.Undo() && DayTemplatePlanner.Periods(session.Document,date) == 7 && session.Redo());
    DocumentIntegrity.EnsureValid(session.Document);
});
Test("V4A702", "7→6교시 기초 수정은 활성 범위 축소, 저장·초기화에서 계획 유지", () => {
    var session = TemplateSession();
    Check(session.Apply(DayTemplatePlanner.Create(session,new(2026,9,14),4).Plan!).IsValid);
    Check(session.Apply(DayTemplatePlanner.Create(session,new(2026,9,14),2).Plan!).IsValid);
    Check(!MonthlyTimetable.WorkingSlots(session.Document).Any(x => x.Day == 1 && x.Period == 7));
    var inactive = session.Document.Current.Slots.Single(x => x.Day == 1 && x.Period == 7);
    Check(!session.Preview(ChangePlanner.Move(session,"lesson1-1",inactive.Id)).IsValid);
    Check(!session.CanUndo && ChangeHistory.NetChanges(session.Document).IsEmpty && session.IsDirty);
    var path = Path.Combine(temporary,"alpha7-base.ttm"); ProjectRepository.WriteAtomic(path,session.Document);
    var loaded = new ProjectSession(ProjectRepository.Read(path),true);
    Check(!loaded.IsDirty && Json(loaded.Document) == Json(session.Document));
    loaded.ResetToBaseline();
    Check(loaded.Document.Current.Lessons.Where(x => x.SlotId.StartsWith("d1")).All(x => x.Subject.StartsWith("2요일")));
    Check(DayTemplatePlanner.OriginalPattern(loaded.Document).Lessons.SequenceEqual(TemplateFixture().Lessons));
    Check(loaded.Document.BaseDayPlans!.Value.Single().SourceDay == 2);
    Check(loaded.Apply(DayTemplatePlanner.Create(loaded,new(2026,9,14),4).Plan!).IsValid);
    Check(DayTemplatePlanner.Periods(loaded.Document,new(2026,9,14)) == 7);
});
Test("V4A703", "학교 계획·원본 패턴은 월 확장과 2주 반복에서 보존, 짧아진 날짜 재생성 방지", () => {
    var session = TemplateSession(true);
    Check(session.Apply(DayTemplatePlanner.Create(session,new(2026,9,14),4).Plan!).IsValid);
    Check(session.Apply(DayTemplatePlanner.Create(session,new(2026,9,17),1).Plan!).IsValid);
    var calendar = session.Document.Calendar!; var slots = session.Document.Current.Slots.ToDictionary(x => x.Id);
    var changedDate = session.Document.Current.Lessons.Where(x => calendar.DateOf(slots[x.SlotId]) == new DateOnly(2026,9,14)).ToArray();
    session.SelectMonth(new(2026,10,1)); session.SelectMonth(new(2026,8,1));
    Check(DayTemplatePlanner.Periods(session.Document,new(2026,9,14)) == 7 && DayTemplatePlanner.Periods(session.Document,new(2026,9,17)) == 6);
    Check(changedDate.All(x => session.Document.Current.Lessons.Contains(x)));
    var path = Path.Combine(temporary,"alpha7-month.ttm"); ProjectRepository.WriteAtomic(path,session.Document);
    Check(Json(ProjectRepository.Read(path)) == Json(session.Document));
    var one = TemplateSession(); Check(one.Apply(DayTemplatePlanner.Create(one,new(2026,9,14),4).Plan!).IsValid);
    var copy = ProjectRepetition.CreateTwoWeekCopy(one.Document);
    Check(copy.Current.Slots.Where(x => x.Week == 2 && x.Day == 1).Max(x => x.Period) == 6);
    Check(copy.Current.Lessons.Where(x => copy.Current.Slots.Single(s => s.Id == x.SlotId) is { Week:2,Day:1 }).All(x => x.Subject.StartsWith("1요일")));
    DocumentIntegrity.EnsureValid(copy);
});
Test("V4A704", "기초 요일은 교체 이력 제외, 이후 보강만 변경 내역에 표시", () => {
    var session = TemplateSession(); Check(session.Apply(DayTemplatePlanner.Create(session,new(2026,9,14),4).Plan!).IsValid);
    Check(ChangeHistory.NetChanges(session.Document).IsEmpty);
    var added = session.Document.Current.Lessons.Single(x => session.Document.Current.Slots.Single(s => s.Id == x.SlotId) is { Day:1,Period:7 });
    Check(session.Apply(ChangePlanner.AssignTeacher(session,added.Id,"t1")).IsValid);
    Check(ChangeHistory.NetChanges(session.Document).Length == 1 && session.Document.HistoryCursor == 1);
});
Test("V4A705", "기초 요일 잠금 승인은 이번 명령만, 같은 학급·교시 잠금 유지, 일반 이동 잠금 유지", () => {
    var session = TemplateSession(); Check(session.Apply(ChangePlanner.ToggleLock(session,"lesson1-1")).IsValid);
    var before = Json(session.Document);
    Check(DayTemplatePlanner.Create(session,new(2026,9,14),4).Plan is null);
    var plan = DayTemplatePlanner.Create(session,new(2026,9,14),4,allowLocked:true).Plan!;
    Check(Json(session.Document) == before && plan.DayTemplate!.AfterLessons.Single(x => x.SlotId == "d1p1").Locked);
    Check(session.Apply(plan).IsValid && session.Document.Current.Lessons.Single(x => x.SlotId == "d1p1").Locked);
    Check(!session.Preview(ChangePlanner.Move(session,"lesson1-1","d1p2")).IsValid);
    Check(!session.CanUndo && ChangeHistory.Operations(session.Document).Count == 0);
    session.SetGradeDateExclusions([new(new(2026,9,14),1)]);
    Check(DayTemplatePlanner.Create(session,new(2026,9,14),2,allowLocked:true).Plan is null);
});
Test("V4A706", "기초 요일 과목 변조·지난 날짜·부재 검증은 잠금 승인으로 우회 불가", () => {
    var session = TemplateSession(); var original = Json(session.Document);
    var valid = DayTemplatePlanner.Create(session,new(2026,9,14),4).Plan!; var payload = valid.DayTemplate!;
    var bad = payload with { AfterLessons = payload.AfterLessons.SetItem(0,payload.AfterLessons[0] with { Subject = "임의 과목" }) };
    Check(!session.Preview(valid with { DayTemplate=bad, Changes=DayTemplatePlanner.Difference(bad.BeforeLessons,bad.AfterLessons) }).IsValid);
    Check(DayTemplatePlanner.Create(session,new(2026,9,19),4).Plan is null && Json(session.Document) == original);
    session.SetCalendar(new(new(2026,9,14),new(2026,9,15))); Check(DayTemplatePlanner.Create(session,new(2026,9,14),4,true).Plan is null);
    var blocked = TemplateFixture() with { Lessons = TemplateFixture().Lessons.Select(x => x.Id == "lesson1-1" ? x with {TeacherId="t1"} : x).ToImmutableArray(), Blocks=[new("t2","d1p1")] };
    var unavailable = Session(blocked); unavailable.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    Check(DayTemplatePlanner.Create(unavailable,new(2026,9,14),4,true).Plan is null);
    var withHiddenBlock = TemplateFixture() with { Slots=TemplateFixture().Slots.Add(new("d1p7",1,1,7)), Blocks=[new("t4","d1p7")] };
    var hidden=Session(withHiddenBlock);hidden.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    Check(hidden.Apply(DayTemplatePlanner.Create(hidden,new(2026,9,14),2).Plan!).IsValid);
    Check(hidden.Document.Current.Blocks.Contains(new TeacherBlock("t4","d1p7")) && DayTemplatePlanner.Periods(hidden.Document,new(2026,9,14))==6);
    Check(DayTemplatePlanner.Create(hidden,new(2026,9,14),4).Plan is null);
});
Test("V4A707", "형식4 요일 이력은 기초 배정으로 전환, 형식5 재열기는 무변경·계획 유지", () => {
    var session = TemplateSession(); var doc = session.Document;
    var plan = DayTemplatePlanner.Create(session,new(2026,9,14),4).Plan!; var day = plan.DayTemplate!; var next = Guid.NewGuid();
    var legacy = doc with { SchemaVersion=4, Baseline=DayTemplatePlanner.Extend(doc.Baseline,day), Current=DayTemplatePlanner.Apply(doc.Current,day,true),
        TemplateSlotIds=day.AdditionalSlots.Select(x => x.Id).ToImmutableArray(), CurrentStateId=next, HistoryCursor=1,
        History=[new(doc.CurrentStateId,next,DateTimeOffset.UtcNow,ChangeKind.DayTemplate,plan.Description,plan.Changes,day)] };
    DocumentIntegrity.EnsureValid(legacy);
    var upgraded = new ProjectSession(legacy,true);
    Check(upgraded.Document.SchemaVersion == 5 && upgraded.IsDirty && upgraded.Document.History.IsEmpty);
    Check(ChangeHistory.NetChanges(upgraded.Document).IsEmpty);
    var path=Path.Combine(temporary,"alpha7-upgraded.ttm"); ProjectRepository.WriteAtomic(path,upgraded.Document);
    var loaded=new ProjectSession(ProjectRepository.Read(path),true); Check(!loaded.IsDirty && Json(loaded.Document) == Json(upgraded.Document));
});
Test("V4A708", "기초 날짜와 연결된 이전 교체는 흡수, 다른 날짜 배정·독립 이력 보존", () => {
    var session=TemplateSession(); Check(session.Apply(ChangePlanner.Move(session,"lesson1-1","d2p2")).IsValid);
    Check(session.Apply(ChangePlanner.AssignTeacher(session,"lesson3-1","t3")).IsValid);
    var before=session.Document.Current.Lessons.Where(x => !x.SlotId.StartsWith("d1")).OrderBy(x=>x.Id).ToArray();
    Check(session.Apply(DayTemplatePlanner.Create(session,new(2026,9,14),4).Plan!).IsValid);
    Check(session.Document.Current.Lessons.Where(x => session.Document.Current.Slots.Single(s=>s.Id==x.SlotId).Day != 1).OrderBy(x=>x.Id).SequenceEqual(before));
    Check(session.Document.History.Length == 1 && session.Document.History[0].Kind == ChangeKind.AssignTeacher);
    Check(session.Undo() && session.Document.Current.Lessons.Single(x=>x.Id=="lesson1-1").SlotId == "d2p2");
    DocumentIntegrity.EnsureValid(session.Document);
});
Test("V4A709", "하루 비우기는 대상 교사의 새 4연강도 배치하고 확인 목록에 포함", () => {
    var state=State() with { Slots=Enumerable.Range(1,3).SelectMany(d=>Enumerable.Range(1,4).Select(p=>new TimeSlot($"d{d}p{p}",1,d,p))).ToImmutableArray(),
        Lessons=Enumerable.Range(1,3).Select(p=>new LessonInstance("root"+p,"c1","수학","t1",$"d1p{p}"))
            .Concat(Enumerable.Range(1,3).Select(p=>new LessonInstance("fixed"+p,"c2","수학","t1",$"d2p{p}")))
            .Concat([new LessonInstance("wed","c2","수학","t1","d3p4")]).ToImmutableArray(), Blocks=[] };
    var session=Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14))); var before=Json(session.Document);
    var request=new TeacherDayRequest("t1",new(2026,9,14),TeacherDayGoal.OtherDates,Direction:SwapDirection.Later);
    var result=TeacherDaySearch.Find(session,request,state.Slots.Select(x=>x.Id).ToHashSet(),CancellationToken.None,allowConsecutive:true);
    Check(result.Plan is not null,result.Message); Check(Json(session.Document) == before);
    Check(TeacherDayReview.Create(state,result.Plan!).Any(x => x.Risk.TeacherId == "t1" && x.Risk.Length >= 4));
    var wider=state with {Slots=state.Slots.AddRange(Enumerable.Range(1,4).Select(p=>new TimeSlot($"d4p{p}",1,4,p)))};
    session=Session(wider); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    result=TeacherDaySearch.Find(session,request,wider.Slots.Select(x=>x.Id).ToHashSet(),CancellationToken.None,allowConsecutive:true);
    Check(result.Plan is not null,result.Message);
    Check(result.Plan!.Changes.Length == 3 && session.Preview(result.Plan!).IsValid);
    Check(result.Plan!.Changes.Where(x=>x.Before.Id.StartsWith("root")).All(x=>wider.Slots.Single(s=>s.Id==x.After.SlotId).Day != 1));
});
Test("V4A710", "오전만 비우기는 오전 수업 분산·오후와 기존 다른 날 수업 유지", () => {
    var state=State() with {Slots=Enumerable.Range(1,3).SelectMany(d=>Enumerable.Range(1,6).Select(p=>new TimeSlot($"d{d}p{p}",1,d,p))).ToImmutableArray(),
        Lessons=[new("morning1","c1","수학","t1","d1p1"),new("morning2","c1","수학","t1","d1p2"),new("afternoon","c1","수학","t1","d1p5"),new("otherDay","c2","수학","t1","d2p2")], Blocks=[]};
    var session=Session(state);session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));var before=Json(session.Document);
    var result=TeacherDaySearch.Find(session,new("t1",new(2026,9,14),TeacherDayGoal.ClearMorning,4,SwapDirection.Later),state.Slots.Select(x=>x.Id).ToHashSet(),CancellationToken.None);
    Check(result.Plan is not null,result.Message); Check(Json(session.Document)==before && session.Apply(result.Plan!).IsValid);
    Check(session.Document.Current.Lessons.Single(x=>x.Id=="afternoon").SlotId=="d1p5" && session.Document.Current.Lessons.Single(x=>x.Id=="otherDay").SlotId=="d2p2");
    Check(session.Document.Current.Lessons.Where(x=>x.Id.StartsWith("morning")).All(x=>state.Slots.Single(s=>s.Id==x.SlotId).Day!=1));
    Check(result.Plan!.Changes.Select(x=>state.Slots.Single(s=>s.Id==x.After.SlotId).Day).Distinct().Count()==2);
});

// Alpha8 regression source. Not compiled or executed in the source preparation environment.
Test("V4A801", "4시간 하루 비우기: 밀려나는 교사도 4연강 허용·최소 8개 변경·원자 적용", () => {
    var state = State() with { Slots = Enumerable.Range(1,2).SelectMany(d => Enumerable.Range(1,4).Select(p => new TimeSlot($"d{d}p{p}",1,d,p))).ToImmutableArray(),
        Lessons = Enumerable.Range(1,4).Select(p => new LessonInstance("root"+p,"c1","수학","t1",$"d1p{p}"))
            .Concat(Enumerable.Range(1,4).Select(p => new LessonInstance("helper"+p,"c1","국어","t2",$"d2p{p}"))).ToImmutableArray(), Blocks = [] };
    var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14))); var before = Json(session.Document);
    var result = TeacherDaySearch.Find(session,new("t1",new(2026,9,14),TeacherDayGoal.OtherDates,Direction:SwapDirection.Later),state.Slots.Select(x => x.Id).ToHashSet(),CancellationToken.None);
    Check(result.Plan is not null,result.Message); Check(Json(session.Document) == before && result.Plan!.Changes.Length == 8);
    var review = TeacherDayReview.Create(state,result.Plan!);
    Check(review.Count == 2 && review.All(x => x.Risk.Length == 4 && x.Lessons.Count == 4));
    Check(review.Any(x => x.Risk.TeacherId == "t2" && x.Risk.Day.Day == 1));
    Check(session.Apply(result.Plan!).IsValid && session.Document.HistoryCursor == 1);
    Check(!session.Document.Current.Lessons.Any(x => x.TeacherId == "t1" && x.SlotId.StartsWith("d1")));
    Check(session.Undo() && DocumentIntegrity.StateEquals(state,session.Document.Current));
});
Test("V4A802", "연강 확인 목록은 새 연강의 기존 수업 포함, 이번 작업과 무관한 기존 연강 제외", () => {
    var state = State() with { Slots = Enumerable.Range(1,2).SelectMany(d => Enumerable.Range(1,6).Select(p => new TimeSlot($"d{d}p{p}",1,d,p))).ToImmutableArray(),
        Lessons = [new("root","c1","수학","t1","d1p6"),new("fixed1","c1","수학","t1","d2p1"),new("fixed2","c1","수학","t1","d2p2"),
            new("old1","c2","국어","t2","d1p1"),new("old2","c2","국어","t2","d1p2"),new("old3","c2","국어","t2","d1p3")], Blocks = [] };
    var session = Session(state); var plan = ChangePlanner.Move(session,"root","d2p3");
    var review = TeacherDayReview.Create(state,plan);
    Check(review.Count == 1 && review[0].Risk.Length == 3);
    Check(review[0].Lessons.Select(x => x.Id).ToHashSet().SetEquals(new[]{"fixed1","fixed2","root"}));
    Check(review[0].Lessons.Single(x => x.Id == "root").SlotId == "d2p3");
    Check(TeacherDayReview.Create(state,ChangePlanner.Move(session,"root","d2p5")).Count == 0);
});
Test("V4A803", "배치 우선에서도 잠금·교사 부재·날짜 제한·취소로 일부 배정이 남지 않음", () => {
    var state = State() with { Slots = Enumerable.Range(1,2).SelectMany(d => Enumerable.Range(1,4).Select(p => new TimeSlot($"d{d}p{p}",1,d,p))).ToImmutableArray(),
        Lessons = Enumerable.Range(1,4).Select(p => new LessonInstance("root"+p,"c1","수학","t1",$"d1p{p}")).ToImmutableArray(), Blocks = [] };
    var request = new TeacherDayRequest("t1",new(2026,9,14),TeacherDayGoal.OtherDates,Direction:SwapDirection.Later);
    foreach (var blocked in new[]{ state with {Lessons=state.Lessons.SetItem(0,state.Lessons[0] with {Locked=true})},
        state with {Blocks=[new("t1","d2p4")]} })
    {
        var session = Session(blocked); session.SetCalendar(new(new(2026,9,14),new(2026,9,14))); var before = Json(session.Document);
        Check(TeacherDaySearch.Find(session,request,blocked.Slots.Select(x => x.Id).ToHashSet(),CancellationToken.None).Plan is null);
        Check(Json(session.Document) == before);
    }
    var unchanged = Session(state); unchanged.SetCalendar(new(new(2026,9,14),new(2026,9,14))); var snapshot = Json(unchanged.Document);
    var scope = state.Slots.Select(x => x.Id).ToHashSet();
    Check(TeacherDaySearch.Find(unchanged,request with {Direction=SwapDirection.Earlier},scope,CancellationToken.None).Plan is null);
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    Throws<OperationCanceledException>(() => TeacherDaySearch.Find(unchanged,request,scope,cancellation.Token));
    Check(Json(unchanged.Document) == snapshot);
});

// Supplied real files are used read-only when passed; generated distribution contains anonymous fixtures only.
// 4.0.3 approval/refusal regressions. Source only; run on Windows when reviewing the release.
ScheduleState PermissionFixture() => State() with {
    Slots = Enumerable.Range(1,4).Select(p => new TimeSlot("p"+p,1,1,p)).ToImmutableArray(),
    Lessons = [new("request","c1","수학","t1","p1"),new("b","c1","국어","t2","p2"),
        new("c","c1","사회","t3","p3"),new("d","c1","과학","t4","p4"),new("busyB","c2","국어","t2","p1")]
};
Test("R40201", "본인 맞밀기에는 출발 교시 공강 없음과 재변경을 구분해서 표시", () => {
    var session = Session(SelfDisplacementState()); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    ApplyMove(session,"source","s3"); var before = Json(session.Document);
    var plan = ChangePlanner.Move(session,"source","s6");
    var row = AutomaticPlanReview.Groups(session,plan,new HashSet<string>{"source"}).Single();
    Check(row.OwnTeacher && row.OriginStillOccupied && row.Requested.Single().Before.Id == "source");
    Check(row.Additional.Single().Before.Id == "own" && row.PreviouslyChanged.SetEquals(new[]{"source"}));
    Check(Json(session.Document) == before);
});
Test("R40202", "교사별 거부 후 다른 교사를 포함한 완성안을 찾으며 목적과 원본 유지", () => {
    var session = Session(PermissionFixture()); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var scope = session.Document.Current.Slots.Select(x => x.Id).ToHashSet(); var before = Json(session.Document);
    var first = CalendarSwapSearch.Find(session,"request","p2",scope,SwapDirection.SameDay,CancellationToken.None);
    Check(first.Plan is not null,first.Message);
    Check(first.Plan!.Changes.Any(x => x.Before.TeacherId == "t3"));
    var restrictions = new AutomaticSearchRestrictions(); restrictions.Refuse(new[]{"t3"},Array.Empty<string>());
    var next = CalendarSwapSearch.Find(session,"request","p2",scope,SwapDirection.SameDay,CancellationToken.None,
        immovable:restrictions.Immovable(session,new HashSet<string>{"request"}));
    Check(next.Plan is not null,next.Message);
    Check(next.Plan!.Changes.All(x => x.Before.TeacherId != "t3"));
    Check(next.Plan.Changes.Single(x => x.Before.Id == "request").After.SlotId == "p2" && session.Preview(next.Plan).IsValid);
    Check(Json(session.Document) == before);
});
Test("R40203", "목적 학급 수업의 교사가 거부하면 부분 적용이나 목적 대체 없이 실패", () => {
    var session = Session(PermissionFixture()); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var before = Json(session.Document); var restrictions = new AutomaticSearchRestrictions();
    restrictions.Refuse(new[]{"t2"},Array.Empty<string>());
    var result = CalendarSwapSearch.Find(session,"request","p2",session.Document.Current.Slots.Select(x => x.Id).ToHashSet(),
        SwapDirection.SameDay,CancellationToken.None,allowConsecutive:true,immovable:restrictions.Immovable(session,new HashSet<string>{"request"}));
    Check(result.Plan is null && Json(session.Document) == before);
    Check(!new AutomaticSearchRestrictions().HasRefusals && session.Document.Current.Lessons.All(x => !x.Locked));
});
Test("R40204", "본인 교사 거부는 추가 수업만 고정하고 직접 요청한 수업은 유지", () => {
    var session = Session(SelfDisplacementState()); var before = Json(session.Document);
    var restrictions = new AutomaticSearchRestrictions(); restrictions.Refuse(new[]{"t1"},Array.Empty<string>());
    var fixedIds = restrictions.Immovable(session,new HashSet<string>{"source"});
    Check(fixedIds.SetEquals(new[]{"own"}) && Json(session.Document) == before);
});
Test("R40205", "연강 허용 재탐색에서도 특정 교사의 연강 미허용은 유지", () => {
    var session = Session(ConsecutiveFixture()); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var scope = session.Document.Current.Slots.Select(x => x.Id).ToHashSet(); var pinned = new HashSet<string>{"own3","own4"};
    var before = Json(session.Document);
    var rejected = CalendarSwapSearch.Find(session,"source","s2",scope,SwapDirection.SameDay,CancellationToken.None,
        allowConsecutive:true,immovable:pinned,avoidConsecutiveTeachers:new HashSet<string>{"t1"});
    Check(rejected.Plan is null);
    var allowed = CalendarSwapSearch.Find(session,"source","s2",scope,SwapDirection.SameDay,CancellationToken.None,
        allowConsecutive:true,immovable:pinned,avoidConsecutiveTeachers:new HashSet<string>{"t2"});
    Check(allowed.Plan is not null && new ConsecutiveTeaching(session.Document.Current).Evaluate(allowed.Plan).Single().TeacherId == "t1");
    Check(Json(session.Document) == before);
});
Test("R40206", "허용은 같은 계획 객체·세션·Revision에만 유효", () => {
    var session = Session(); var plan = ChangePlanner.Move(session,"l1","s3"); var permission = new AutomaticPlanApproval();
    permission.Approve(session,plan); Check(permission.IsApproved(session,plan));
    Check(!permission.IsApproved(session,plan with {}) && !permission.IsApproved(new ProjectSession(session.Document),plan));
    Check(session.Apply(ChangePlanner.ToggleLock(session,"l2")).IsValid && !permission.IsApproved(session,plan));
    permission.Reset(); Check(!permission.IsApproved(session,plan));
});
Test("R40207", "하루 자동 이동도 거부한 교사의 수업은 고정하며 원본 유지", () => {
    var state = State() with { Lessons = [new("a","c1","수학","t1","s5"),new("b","c1","국어","t2","s1"),new("busyB","c2","국어","t2","s5")] };
    var session = Session(state); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var scope = state.Slots.Select(x => x.Id).ToHashSet(); var before = Json(session.Document);
    var request = new TeacherDayRequest("t1",new(2026,9,14),TeacherDayGoal.Morning);
    var allowed = TeacherDaySearch.Find(session,request,scope,CancellationToken.None);
    Check(allowed.Plan is not null,allowed.Message);
    var restrictions = new AutomaticSearchRestrictions(); restrictions.Refuse(new[]{"t2"},Array.Empty<string>());
    var denied = TeacherDaySearch.Find(session,request,scope,CancellationToken.None,allowConsecutive:true,
        immovable:restrictions.Immovable(session,new HashSet<string>{"a"}));
    Check(denied.Plan is null && Json(session.Document) == before);
});

Test("R40208", "AI로 변경한 출발 수업과 교체 교사의 수업 모두 재변경 표시", () => {
    var session = Session(PermissionFixture()); session.SetCalendar(new(new(2026,9,14),new(2026,9,14)));
    var first = CalendarSwapSearch.Find(session,"request","p2",session.Document.Current.Slots.Select(x => x.Id).ToHashSet(),SwapDirection.SameDay,CancellationToken.None);
    Check(first.Plan is not null && session.Apply(first.Plan).IsValid);
    var before = Json(session.Document); var next = ChangePlanner.Move(session,"request","p1");
    var groups = AutomaticPlanReview.Groups(session,next,new HashSet<string>{"request"});
    Check(groups.Single(x => x.TeacherId == "t1").PreviouslyChanged.Contains("request"));
    var helper = groups.Single(x => x.TeacherId == "t3");
    Check(helper.Requested.IsEmpty && helper.Additional.Single().Before.Id == "c" && helper.PreviouslyChanged.Contains("c"));
    Check(Json(session.Document) == before);
});

Test("R40209", "미배정 교사 항목을 거부해도 같은 안을 반복하지 않도록 고정", () => {
    var state = State() with { Lessons = [new("source","c1","수학","t1","s1"),new("unknown","c1","사회",null,"s2")] };
    var session = Session(state); var before = Json(session.Document); var requested = new HashSet<string>{"source"};
    var row = AutomaticPlanReview.Groups(session,ChangePlanner.Move(session,"source","s2"),requested).Single(x => x.TeacherId == "");
    Check(row.Name == "교사 미배정" && row.Additional.Single().Before.Id == "unknown");
    var restrictions = new AutomaticSearchRestrictions(); restrictions.Refuse(new[]{row.TeacherId},Array.Empty<string>());
    Check(restrictions.Immovable(session,requested).SetEquals(new[]{"unknown"}) && Json(session.Document) == before);
});

if (args.Length > 0 && Directory.Exists(args[0]))
{
    var files=Directory.GetFiles(args[0],"*.CSV").Order().ToArray();
    for(int i=0;i<files.Length;i++)
    {
        var file=files[i];var n=i;
        Test("L00"+(n+1),"첨부 CSV "+(n+1)+" 18학급·37교사·630/1260수업·18담임",()=>{
            var table=TabularReader.Read(file)[0];Check(table.Encoding=="CP949");
            foreach(bool repeat in new[]{false,true}){
                var p=legacy.Parse(table,repeatSingleWeek:repeat);Check(p.CanImport,string.Join(";",p.Issues.Where(x=>x.Severity==Severity.Error).Select(x=>x.Message)));
                var s=p.Document!.Current;Check(s.Classes.Length==18);Check(s.Teachers.Length==37);Check(s.Lessons.Length==(repeat?1260:630));Check(s.Classes.Count(x=>x.HomeroomTeacherId is not null)==18);
                var path=Path.Combine(temporary,$"legacy-{n}-{repeat}.ttm");ProjectRepository.WriteAtomic(path,p.Document);Check(Json(ProjectRepository.Read(path))==Json(p.Document));
            }
        });
    }
}

Test("P001", "확대 자료 60학급·120교사·4800수업 검증/저장/후보", () => {
    var teachers=Enumerable.Range(0,120).Select(i=>new Teacher("t"+i,"교사"+i)).ToImmutableArray();
    var classes=Enumerable.Range(0,60).Select(i=>new ClassGroup("c"+i,"학급"+i)).ToImmutableArray();
    var slots=Enumerable.Range(0,80).Select(i=>new TimeSlot("s"+i,i/40+1,i%40/8+1,i%8+1)).ToImmutableArray();
    var lessons=classes.SelectMany((c,i)=>slots.Select((slot,j)=>new LessonInstance($"l{i}-{j}",c.Id,"수학","t"+((i+j)%120),slot.Id))).ToImmutableArray();
    var state=new ScheduleState(teachers,classes,slots,lessons,[]);
    var watch=Stopwatch.StartNew();Check(ConstraintValidator.Validate(state).IsValid);metrics["validate_4800_ms"]=watch.Elapsed.TotalMilliseconds;
    var session=Session(state);watch.Restart();var list=ChangePlanner.DirectCandidates(session,"l0-0");metrics["candidates_4800_ms"]=watch.Elapsed.TotalMilliseconds;Check(list.Length>0);
    var path=Path.Combine(temporary,"large.ttm");watch.Restart();ProjectRepository.WriteAtomic(path,session.Document);metrics["save_4800_ms"]=watch.Elapsed.TotalMilliseconds;
    watch.Restart();Check(ProjectRepository.Read(path).Current.Lessons.Length==4800);metrics["load_4800_ms"]=watch.Elapsed.TotalMilliseconds;
});

WorkPeriodCases.Run(Test, Check);

var output=args.Length>1?args[1]:Path.Combine(Environment.CurrentDirectory,"test-results.json");
File.WriteAllText(output,JsonSerializer.Serialize(new { utc=DateTimeOffset.UtcNow,runtime=Environment.Version.ToString(),os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,passed=results.Count(x=>x.Passed),failed=results.Count(x=>!x.Passed),metrics,tests=results },new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"RESULT {results.Count(x=>x.Passed)}/{results.Count} passed; report: {output}");
Directory.Delete(temporary,true);
return results.All(x=>x.Passed)?0:1;

internal sealed record TestResult(string Id,string Name,bool Passed,double Milliseconds,string? Error);
