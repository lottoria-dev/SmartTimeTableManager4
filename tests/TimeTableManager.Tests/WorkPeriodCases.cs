using System.Collections.Immutable;
using System.Text.Json;
using TimeTableManager.Application;
using TimeTableManager.Domain;
using TimeTableManager.Infrastructure;

internal static class WorkPeriodCases
{
    public static void Run(Action<string, string, Action> test, Action<bool, string> assert)
    {
        void Check(bool condition, string message = "작업 기간 검증 실패") => assert(condition, message);
        ScheduleState Pattern(int cycle = 1) => new([new("a", "교사A"), new("b", "교사B")], [new("c", "1-1")],
            Enumerable.Range(1, cycle).SelectMany(w => Enumerable.Range(1, 5).SelectMany(d =>
                Enumerable.Range(1, 3).Select(p => new TimeSlot($"w{w}d{d}p{p}", w, d, p)))).ToImmutableArray(),
            Enumerable.Range(1, cycle).SelectMany(w => Enumerable.Range(1, 5).Select(d =>
                new LessonInstance($"w{w}l{d}", "c", "과목" + d, w % 2 == 1 ? "a" : "b", $"w{w}d{d}p1"))).ToImmutableArray(), []);
        ProjectDocument Create(DateOnly today, int weeks = 1, int cycle = 1) => WorkPeriod.Prepare(ProjectDocument.Create("기간 시험", Pattern(cycle)), today, weeks);
        string Slot(ProjectDocument document, DateOnly date, int period = 1) => document.Current.Slots.Single(x => document.Calendar!.DateOf(x) == date && x.Period == period).Id;
        string Lesson(ProjectDocument document, DateOnly date) => document.Current.Lessons.Single(x => x.SlotId == Slot(document, date)).Id;
        string Json(ProjectDocument document) => JsonSerializer.Serialize(document, ProjectRepository.JsonOptions);

        test("WP001", "금요일 경계: 0·1·2·3주, 월말·연말·주말", () =>
        {
            var today = new DateOnly(2026, 10, 28);
            Check(Enumerable.Range(0, 4).Select(w => BoardDateRange.Friday(today, w)).SequenceEqual(new[]
                { new DateOnly(2026,10,30), new DateOnly(2026,11,6), new DateOnly(2026,11,13), new DateOnly(2026,11,20) }));
            Check(BoardDateRange.Friday(new(2026,12,30), 1) == new DateOnly(2027,1,8));
            Check(!BoardDateRange.IsVisible(new(2026,10,17), new(2026,10,17), 0));
            Check(BoardDateRange.IsVisible(new(2026,10,19), new(2026,10,18), 1));
            Check(!BoardDateRange.IsVisible(new(2026,10,24), new(2026,10,18), 2));
        });
        test("WP002", "기간 생성은 다음 달까지 연속, 현재 월 전체 조회 자료도 유지", () =>
        {
            var document = Create(new(2026,10,28), 3); var calendar = document.Calendar!;
            Check(document.SchemaVersion == 6 && calendar.EditableFrom == new DateOnly(2026,10,28) && calendar.EditableUntil == new DateOnly(2026,11,20));
            Check(Slot(document, new(2026,10,1)) is not null && Slot(document, new(2026,11,20)) is not null);
            Check(WorkPeriod.EditableSlots(document).All(x => calendar.DateOf(x) >= new DateOnly(2026,10,28) && calendar.DateOf(x) <= new DateOnly(2026,11,20) && x.Day <= 5));
            Check(WorkPeriod.EditableSlots(document).Select(calendar.DateOf).Distinct().Count() == 18);
            Check(document.Current.Slots.GroupBy(x => (calendar.DateOf(x), x.Period)).All(g => g.Count() == 1));
        });
        test("WP003", "기간 축소·재확장: 기존 배정·수업 ID·이력 불변", () =>
        {
            var session = new ProjectSession(Create(new(2026,10,28), 3), true);
            string id = Lesson(session.Document, new(2026,10,28));
            Check(session.Apply(ChangePlanner.Move(session, id, Slot(session.Document, new(2026,11,2), 2))).IsValid);
            var current = session.Document.Current; var history = session.Document.History;
            session.SetWorkPeriod(new(2026,10,28), 0);
            Check(session.Document.Current.Lessons.SequenceEqual(current.Lessons) && session.Document.History.SequenceEqual(history));
            Check(!session.Document.Calendar!.CanEdit(new DateOnly(2026,11,2)) && !session.CanUndo);
            session.SetWorkPeriod(new(2026,10,28), 3);
            Check(session.Document.Current.Lessons.SequenceEqual(current.Lessons) && session.Document.History.SequenceEqual(history));
            Check(session.CanUndo && session.Undo() && session.Redo());
        });
        test("WP004", "월 전체 조회는 편집 기간 불변, 이전 달 확장에도 실제 날짜 보존", () =>
        {
            var session = new ProjectSession(Create(new(2026,10,28), 1));
            var calendar = session.Document.Calendar!;
            var dates = session.Document.Current.Slots.ToDictionary(x => x.Id, calendar.DateOf);
            session.IncludeViewMonth(new(2026,9,1));
            Check(session.Document.Calendar!.EditableFrom == calendar.EditableFrom && session.Document.Calendar.EditableUntil == calendar.EditableUntil);
            Check(session.Document.Current.Slots.Where(x => dates.ContainsKey(x.Id)).All(x => session.Document.Calendar.DateOf(x) == dates[x.Id]));
            Check(!session.Document.Calendar.CanEdit(new DateOnly(2026,9,30)) && !session.Document.Calendar.CanEdit(new DateOnly(2026,11,9)));
            session.IncludeViewMonth(new(2026,12,1));
            Check(session.Document.Calendar.EditableUntil == calendar.EditableUntil);
            Check(!session.Document.Calendar.CanEdit(new DateOnly(2026,12,1)));
        });
        test("WP005", "구버전 월 파일의 이동·보강·이력은 주간 작업 기간 전환 후 저장·재열기 보존", () =>
        {
            var old = new ProjectSession(MonthlyTimetable.EnsureMonth(ProjectDocument.Create("이전 자료", Pattern()), new(2026,9,1)));
            var id = Lesson(old.Document, new(2026,9,30)); var target = Slot(old.Document, new(2026,10,1), 2);
            Check(old.Apply(ChangePlanner.Move(old, id, target)).IsValid && old.Apply(ChangePlanner.AssignTeacher(old, id, "b")).IsValid);
            var history = old.Document.History; var lessons = old.Document.Current.Lessons.ToDictionary(x => x.Id);
            old.SetWorkPeriod(new(2026,9,30), 1);
            Check(old.Document.SchemaVersion == 6 && old.Document.History.SequenceEqual(history));
            Check(old.Document.Current.Lessons.Where(x => lessons.ContainsKey(x.Id)).All(x => x == lessons[x.Id]));
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ttm");
            try { ProjectRepository.WriteAtomic(path, old.Document); Check(Json(ProjectRepository.Read(path)) == Json(old.Document)); }
            finally { if (File.Exists(path)) File.Delete(path); }
            Check(old.Undo() && old.Undo() && old.Redo() && old.Redo());
        });
        test("WP006", "재열기 기준일 갱신: 어제는 조회만, 앞으로의 수업은 편집 가능", () =>
        {
            var document = Create(new(2026,10,28), 2);
            var today = new DateOnly(2026,10,29); var session = new ProjectSession(document, true);
            session.SetWorkPeriod(today, document.Calendar!.WeeksAhead);
            Check(session.Document.Calendar!.EditableFrom == today && session.Document.Calendar.WeeksAhead == 2);
            Check(!session.Preview(ChangePlanner.Move(session, Lesson(session.Document, new(2026,10,28)), Slot(session.Document, today, 2))).IsValid);
            Check(session.Preview(ChangePlanner.Move(session, Lesson(session.Document, today), Slot(session.Document, today, 2))).IsValid);
            Check(session.IsDirty);
        });
        test("WP007", "수동 추천: 다음 달도 동일 조건, 기간 밖은 배치 차단", () =>
        {
            var session = new ProjectSession(Create(new(2026,10,28), 1));
            var draft = new ManualChainDraft(session);
            Check(draft.Take(Lesson(session.Document, new(2026,10,28))) is null);
            Check(draft.Advice(Slot(session.Document, new(2026,10,30), 2)).Recommended);
            Check(draft.Advice(Slot(session.Document, new(2026,11,2), 2)).Recommended);
            Check(!draft.Advice(Slot(session.Document, new(2026,10,27), 2)).Allowed);
            Check(draft.Put(Slot(session.Document, new(2026,11,2), 2)) is null && draft.Held.IsEmpty);
            var plan = draft.Plan(); Check(plan is not null && session.Apply(plan).IsValid);
        });
        test("WP008", "AI 목적 고정은 기간 제한 적용, 기간 확장 후 다음 달 목적 탐색", () =>
        {
            var session = new ProjectSession(Create(new(2026,10,28), 0));
            session.IncludeViewMonth(new(2026,11,1));
            var id = Lesson(session.Document, new(2026,10,28)); var target = Slot(session.Document, new(2026,11,2), 2);
            var all = MonthlyTimetable.ActiveSlots(session.Document).Select(x => x.Id).ToHashSet();
            Check(CalendarSwapSearch.Find(session, id, target, all, SwapDirection.Later, CancellationToken.None).Plan is null);
            session.SetWorkPeriod(new(2026,10,28), 1);
            var found = CalendarSwapSearch.Find(session, id, target, all, SwapDirection.Later, CancellationToken.None);
            Check(found.Plan is not null && found.Plan.Changes.Single(x => x.Before.Id == id).After.SlotId == target, found.Message);
        });
        test("WP009", "기초 요일 적용도 작업 기간 제한, 적용 후 기간 확장에 새 기초 배정 보존", () =>
        {
            var session = new ProjectSession(Create(new(2026,10,28), 0));
            Check(DayTemplatePlanner.Create(session, new(2026,10,27), 4).Plan is null);
            session.IncludeViewMonth(new(2026,11,1));
            Check(DayTemplatePlanner.Create(session, new(2026,11,2), 4).Plan is null);
            var plan = DayTemplatePlanner.Create(session, new(2026,10,28), 4).Plan!;
            Check(session.Apply(plan).IsValid && session.Document.SchemaVersion == 6);
            var day = session.Document.Current.Lessons.Where(x => session.Document.Calendar!.DateOf(session.Document.Current.Slots.Single(s => s.Id == x.SlotId)) == new DateOnly(2026,10,28)).ToArray();
            session.SetWorkPeriod(new(2026,10,28), 3);
            Check(day.All(x => session.Document.Current.Lessons.Contains(x)));
            Check(session.Document.BaseDayPlans!.Value.Any(x => x.Date == new DateOnly(2026,10,28)));
        });
        test("WP010", "2주 기초 시간표는 월 경계를 넘어도 주기·기준 월요일 보존", () =>
        {
            var document = Create(new(2026,10,28), 3, 2);
            Check(document.Current.Lessons.Single(x => x.SlotId == Slot(document, new(2026,10,26))).TeacherId == "a");
            Check(document.Current.Lessons.Single(x => x.SlotId == Slot(document, new(2026,11,2))).TeacherId == "b");
            Check(document.Current.Lessons.Single(x => x.SlotId == Slot(document, new(2026,11,9))).TeacherId == "a");
            var anchor = document.Monthly!.PatternMonday;
            document = WorkPeriod.IncludeMonth(document, new(2026,9,1));
            Check(document.Monthly!.PatternMonday == anchor && document.Current.Lessons.Single(x => x.SlotId == Slot(document, new(2026,11,2))).TeacherId == "b");
        });
        test("WP011", "윤년·연말 날짜 확장은 평일 연속·기간 축소 중복 없음", () =>
        {
            foreach (var today in new[] { new DateOnly(2024,2,28), new DateOnly(2026,12,30), new DateOnly(2026,10,18) })
            {
                var document = Create(today, 3);
                document = WorkPeriod.Prepare(WorkPeriod.Prepare(document, today, 0), today, 3);
                Check(document.Current.Slots.GroupBy(x => (document.Calendar!.DateOf(x), x.Period)).All(g => g.Count() == 1));
                Check(document.Current.Lessons.Select(x => x.Id).Distinct().Count() == document.Current.Lessons.Length);
                DocumentIntegrity.EnsureValid(document);
            }
        });
        test("WP012", "잠금·행사 제외는 다음 달 수업에서도 유지", () =>
        {
            var session = new ProjectSession(Create(new(2026,10,28), 2));
            var id = Lesson(session.Document, new(2026,11,2));
            Check(session.Apply(ChangePlanner.ToggleLock(session, id)).IsValid);
            session.SetGradeDateExclusions([new(new(2026,11,3), 1)]);
            session.SetWorkPeriod(new(2026,10,28), 3);
            Check(!session.Preview(ChangePlanner.Move(session, id, Slot(session.Document, new(2026,11,2), 2))).IsValid);
            Check(!session.Preview(ChangePlanner.AssignTeacher(session, Lesson(session.Document, new(2026,11,3)), "b")).IsValid);
        });
    }
}
