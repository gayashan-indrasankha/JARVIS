using System.Text.Json;
using Jarvis.Core.ProjectIntelligence;
using Jarvis.Core.ProjectLearning;

namespace Jarvis.Core.Tests.ProjectLearning;

public sealed class ProjectLearningServiceTests
{
    [Fact]
    public async Task TutorUsesEvidenceAndProgressesWithoutLosingSession()
    {
        Fixture fixture = new();
        ProjectLearningTurnResult started = await fixture.Service.StartTutorAsync(
            fixture.RepositoryPath,
            TutorLevel.Foundation,
            "architecture",
            askBeforeTell: true,
            ModelProfile.Fast,
            CancellationToken.None);

        ProjectLearningTurnResult continued = await fixture.Service.ContinueTutorAsync(
            started.SessionId,
            TutorInteractionKind.GoDeeper,
            "Explain the boundary",
            CancellationToken.None);

        Assert.Equal(started.SessionId, continued.SessionId);
        Assert.Equal(TutorLevel.Architecture, continued.TutorTurn!.Level);
        Assert.NotEmpty(continued.TutorTurn.Statements.Single(static item =>
            item.Kind == LearningStatementKind.ProjectFact).Evidence);
        Assert.Contains(continued.TutorTurn.Statements, static item =>
            item.Kind == LearningStatementKind.DesignAlternative && item.Evidence.Count == 0);
        Assert.True(fixture.Model.LastTutorRequest!.AskBeforeTell);
        Assert.Equal(2, fixture.Store.Sessions[started.SessionId].TutorTurns.Count);
    }

    [Fact]
    public async Task WeakInterviewAnswerCreatesTargetedFollowUpInSameDimension()
    {
        Fixture fixture = new();
        const string untrustedDisclosure = "The project uses a fabricated Redis cache.";
        fixture.Model.Assessment = FakeLearningModel.WeakAssessment with
        {
            Rationale = untrustedDisclosure,
            Correction = untrustedDisclosure,
        };
        ProjectLearningTurnResult started = await fixture.Service.StartInterviewAsync(
            fixture.RepositoryPath,
            InterviewDifficulty.Junior,
            5,
            ModelProfile.Fast,
            CancellationToken.None);

        ProjectLearningTurnResult result = await fixture.Service.SubmitInterviewAnswerAsync(
            started.SessionId,
            "I am not sure.",
            CancellationToken.None);

        Assert.True(result.Evaluation!.RequiresTargetedFollowUp);
        Assert.True(result.InterviewQuestion!.IsFollowUp);
        Assert.Equal(started.InterviewQuestion!.Dimension, result.InterviewQuestion.Dimension);
        Assert.Equal(started.InterviewQuestion.QuestionId, result.InterviewQuestion.ParentQuestionId);
        Assert.Equal(["Project fact 0"], started.InterviewQuestion.ExpectedConcepts);
        Assert.Equal(1, fixture.Model.QuestionGenerationCount);
        Assert.Empty(result.Evaluation.Corrections);
        Assert.Empty(result.Evaluation.Gaps);
        Assert.DoesNotContain(untrustedDisclosure, result.Evaluation.Rationale, StringComparison.Ordinal);
        Assert.DoesNotContain(untrustedDisclosure, result.InterviewQuestion.Text, StringComparison.Ordinal);
        Assert.All(result.Evaluation.Scores, score =>
            Assert.DoesNotContain(untrustedDisclosure, score.Rationale, StringComparison.Ordinal));
        ProjectLearningSessionSnapshot stored = fixture.Store.Sessions[started.SessionId];
        LearningStatement correction = Assert.Single(stored.InterviewTurns[0].Evaluation.Corrections);
        Assert.Equal("Project fact 0", correction.Text);
        Assert.NotEmpty(correction.Evidence);
        Assert.Contains("Project fact 0", stored.InterviewTurns[0].Evaluation.Gaps);
        Assert.DoesNotContain(untrustedDisclosure, stored.Transcript[^1].Text, StringComparison.Ordinal);
        Assert.All(result.Evaluation.Scores, static score => Assert.InRange(score.Score, 0, 4));
        Assert.Equal(Enum.GetValues<ScoreDimension>().Length, result.Evaluation.Scores.Count);
    }

    [Fact]
    public async Task FiveQuestionInterviewTerminatesAndRevisionUsesWeakestTopic()
    {
        Fixture fixture = new();
        fixture.Model.Assessment = FakeLearningModel.WeakAssessment;
        ProjectLearningTurnResult current = await fixture.Service.StartInterviewAsync(
            fixture.RepositoryPath,
            InterviewDifficulty.Internship,
            5,
            ModelProfile.Fast,
            CancellationToken.None);

        for (int index = 0; index < 5; index++)
        {
            current = await fixture.Service.SubmitInterviewAnswerAsync(
                current.SessionId,
                "My answer uses the repository evidence.",
                CancellationToken.None);
        }

        Assert.True(current.ReadyToComplete);
        Assert.Null(current.InterviewQuestion);
        ProjectLearningTurnResult ended = await fixture.Service.EndSessionAsync(
            current.SessionId,
            CancellationToken.None);

        Assert.Equal(LearningSessionStatus.Completed, ended.Status);
        Assert.Equal(10, ended.Report!.Categories.Count);
        Assert.NotEmpty(ended.Report.WeakAreas);
        Assert.NotEmpty(ended.Report.RevisionTopics);

        ProjectLearningTurnResult revision = await fixture.Service.StartRevisionAsync(
            fixture.RepositoryPath,
            ModelProfile.Fast,
            CancellationToken.None);

        Assert.Equal(LearningSessionKind.Tutor, revision.Kind);
        Assert.True(fixture.Model.LastTutorRequest!.AskBeforeTell);
        Assert.Contains(
            fixture.Store.Sessions[current.SessionId].Report!.RevisionTopics[0],
            fixture.Model.LastTutorRequest.Topic,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContextSelectionIsBoundedAndRequiresProjectFactEvidence()
    {
        Fixture fixture = new(maximumEvidenceItems: 3, maximumContextCharacters: 4_096);
        fixture.Evidence.Claims = Enumerable.Range(0, 20)
            .Select(index => Fixture.CreateClaim(index, new string('x', 700)))
            .ToArray();
        fixture.Model.EvidenceIndexes = [0, 1, 2];

        ProjectLearningTurnResult result = await fixture.Service.StartTutorAsync(
            fixture.RepositoryPath,
            TutorLevel.Implementation,
            "service flow",
            askBeforeTell: false,
            ModelProfile.Fast,
            CancellationToken.None);

        Assert.InRange(fixture.Model.LastTutorRequest!.EvidenceClaims.Count, 1, 3);
        Assert.True(result.TutorTurn!.ContextBudget.UsedCharacters <= 4_096);
        Assert.True(JsonSerializer.Serialize(result).Length < 16 * 1024);

        fixture.Evidence.Claims =
        [
            new ProjectClaim(
                ProjectKnowledgeClassification.GeneralSoftwareEngineeringKnowledge,
                "General guidance",
                []),
        ];
        ProjectLearningException error = await Assert.ThrowsAsync<ProjectLearningException>(() =>
            fixture.Service.StartTutorAsync(
                fixture.RepositoryPath,
                TutorLevel.Foundation,
                "overview",
                false,
                ModelProfile.Fast,
                CancellationToken.None).AsTask());
        Assert.Equal("project_evidence_required", error.Code);
    }

    [Fact]
    public async Task CancellationStopsBeforeSessionOrProfileIsCreated()
    {
        Fixture fixture = new();
        fixture.Evidence.BlockUntilCancellation = true;
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.StartTutorAsync(
            fixture.RepositoryPath,
            TutorLevel.Foundation,
            "overview",
            false,
            ModelProfile.Fast,
            cancellation.Token).AsTask());

        Assert.Empty(fixture.Store.Sessions);
        Assert.Equal(0, fixture.Profile.BeginCount);
    }

    [Fact]
    public async Task DeepFallbackIsReportedAndProfileReturnsToFastAtEnd()
    {
        Fixture fixture = new();
        fixture.Profile.Fallback = true;
        ProjectLearningTurnResult started = await fixture.Service.StartTutorAsync(
            fixture.RepositoryPath,
            TutorLevel.Foundation,
            "overview",
            false,
            ModelProfile.Deep,
            CancellationToken.None);

        Assert.Equal(ModelProfile.Deep, started.Profile.Requested);
        Assert.Equal(ModelProfile.Fast, started.Profile.Selected);
        Assert.True(started.Profile.FellBack);

        await fixture.Service.EndSessionAsync(started.SessionId, CancellationToken.None);
        Assert.Equal(1, fixture.Profile.EndCount);
    }

    [Fact]
    public async Task EndSessionDoesNotReportSuccessUntilProfileRestorationSucceeds()
    {
        Fixture fixture = new();
        ProjectLearningTurnResult started = await fixture.Service.StartTutorAsync(
            fixture.RepositoryPath,
            TutorLevel.Foundation,
            "overview",
            false,
            ModelProfile.Deep,
            CancellationToken.None);
        fixture.Profile.ThrowOnEnd = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.EndSessionAsync(started.SessionId, CancellationToken.None).AsTask());
        Assert.Equal(
            LearningSessionStatus.Completed,
            fixture.Store.Sessions[started.SessionId].Status);

        fixture.Profile.ThrowOnEnd = false;
        ProjectLearningTurnResult retried = await fixture.Service.EndSessionAsync(
            started.SessionId,
            CancellationToken.None);

        Assert.Equal(LearningSessionStatus.Completed, retried.Status);
        Assert.Equal(2, fixture.Profile.EndCount);
    }

    [Fact]
    public async Task InterviewQuestionCannotInventAnEvidenceIndex()
    {
        Fixture fixture = new();
        fixture.Model.EvidenceIndexes = [99];

        ProjectLearningException exception = await Assert.ThrowsAsync<ProjectLearningException>(() =>
            fixture.Service.StartInterviewAsync(
                fixture.RepositoryPath,
                InterviewDifficulty.Internship,
                5,
                ModelProfile.Fast,
                CancellationToken.None).AsTask());

        Assert.Equal("interview_question_evidence_required", exception.Code);
        Assert.Equal(1, fixture.Profile.EndCount);
        Assert.Empty(fixture.Store.Sessions);
    }

    [Fact]
    public async Task ConcurrentAnswersAreSerializedIntoDistinctInterviewTurns()
    {
        Fixture fixture = new();
        ProjectLearningTurnResult started = await fixture.Service.StartInterviewAsync(
            fixture.RepositoryPath,
            InterviewDifficulty.Internship,
            5,
            ModelProfile.Fast,
            CancellationToken.None);

        Task<ProjectLearningTurnResult> first = fixture.Service.SubmitInterviewAnswerAsync(
            started.SessionId,
            "First answer",
            CancellationToken.None).AsTask();
        Task<ProjectLearningTurnResult> second = fixture.Service.SubmitInterviewAnswerAsync(
            started.SessionId,
            "Second answer",
            CancellationToken.None).AsTask();
        await Task.WhenAll(first, second);

        ProjectLearningSessionSnapshot stored = fixture.Store.Sessions[started.SessionId];
        Assert.Equal(2, stored.InterviewTurns.Count);
        Assert.NotEqual(
            stored.InterviewTurns[0].Question.QuestionId,
            stored.InterviewTurns[1].Question.QuestionId);
        Assert.Equal(["First answer", "Second answer"], stored.InterviewTurns.Select(static turn => turn.UserAnswer));
    }

    private sealed class Fixture
    {
        public Fixture(int maximumEvidenceItems = 10, int maximumContextCharacters = 12_000)
        {
            RepositoryPath = Path.Combine(Path.GetTempPath(), "jarvis-learning-fixture");
            Evidence = new FakeEvidenceSource { Claims = [CreateClaim(0, "The host composes services.")] };
            Model = new FakeLearningModel();
            Store = new FakeSessionStore();
            Profile = new FakeProfileRouter();
            Service = new ProjectLearningService(
                Model,
                Evidence,
                Profile,
                Store,
                new ProjectLearningConfiguration(
                    maximumContextCharacters,
                    maximumEvidenceItems,
                    4,
                    5,
                    20),
                TimeProvider.System);
        }

        public string RepositoryPath { get; }

        public FakeEvidenceSource Evidence { get; }

        public FakeLearningModel Model { get; }

        public FakeSessionStore Store { get; }

        public FakeProfileRouter Profile { get; }

        public ProjectLearningService Service { get; }

        public static ProjectClaim CreateClaim(int index, string excerpt) => new(
            ProjectKnowledgeClassification.ProjectFact,
            $"Project fact {index}",
            [new ProjectEvidence(
                $"src/Feature{index}.cs",
                index + 1,
                index + 2,
                $"Feature{index}",
                excerpt,
                $"hash-{index}")]);
    }

    private sealed class FakeLearningModel : IProjectLearningModel
    {
        public static InterviewAnswerAssessmentOutput WeakAssessment { get; } = new(
            [],
            [0],
            false,
            false,
            false,
            false,
            false,
            false,
            1,
            1,
            "The answer missed the project evidence.",
            "The project composes the service in its host.",
            [0]);

        public InterviewAnswerAssessmentOutput Assessment { get; set; } = new(
            [0, 1],
            [],
            true,
            true,
            true,
            true,
            true,
            true,
            4,
            4,
            "The answer was grounded and clear.",
            null,
            []);

        public TutorGenerationRequest? LastTutorRequest { get; private set; }

        public IReadOnlyList<int> EvidenceIndexes { get; set; } = [0];

        public int QuestionGenerationCount { get; private set; }

        public ValueTask<TutorGenerationOutput> GenerateTutorTurnAsync(
            TutorGenerationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastTutorRequest = request;
            return ValueTask.FromResult(new TutorGenerationOutput(
                "This explanation separates project facts from general principles.",
                request.AskBeforeTell ? "What evidence supports that boundary?" : null,
                ["composition", "boundary"],
                EvidenceIndexes,
                ["clear reasoning"],
                ["failure handling"],
                ["A separate adapter could replace the current implementation."]));
        }

        public ValueTask<InterviewQuestionGenerationOutput> GenerateInterviewQuestionAsync(
            InterviewQuestionGenerationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuestionGenerationCount++;
            return ValueTask.FromResult(new InterviewQuestionGenerationOutput(
                request.IsFollowUp
                    ? "Which repository evidence corrects the gap in your previous answer?"
                    : $"Explain the project evidence for {request.Dimension}.",
                ["composition", "boundary"],
                EvidenceIndexes));
        }

        public ValueTask<InterviewAnswerAssessmentOutput> AssessInterviewAnswerAsync(
            InterviewAnswerAssessmentRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Assessment);
        }
    }

    private sealed class FakeEvidenceSource : IProjectLearningEvidenceSource
    {
        public ProjectClaim[] Claims { get; set; } = [];

        public bool BlockUntilCancellation { get; set; }

        public ValueTask<GroundedProjectAnswer> GetTutorEvidenceAsync(
            string repositoryPath,
            TutorLevel level,
            string topic,
            CancellationToken cancellationToken) => CreateAsync(cancellationToken);

        public ValueTask<GroundedProjectAnswer> GetInterviewEvidenceAsync(
            string repositoryPath,
            InterviewDimension dimension,
            CancellationToken cancellationToken) => CreateAsync(cancellationToken);

        private ValueTask<GroundedProjectAnswer> CreateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BlockUntilCancellation)
            {
                return new ValueTask<GroundedProjectAnswer>(Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken)
                    .ContinueWith(
                        _ => Create(),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default));
            }

            return ValueTask.FromResult(Create());
        }

        private GroundedProjectAnswer Create() => new(
            Claims,
            new ProjectQueryMetrics(
                1,
                Claims.Length,
                new ProjectContextBudget(12_000, 1_000, 250, Claims.Length, false)),
            "snapshot-1");
    }

    private sealed class FakeSessionStore : IProjectLearningSessionStore
    {
        public Dictionary<Guid, ProjectLearningSessionSnapshot> Sessions { get; } = [];

        public ValueTask SaveAsync(ProjectLearningSessionSnapshot session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sessions[session.SessionId] = session;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ProjectLearningSessionSnapshot?> LoadAsync(
            Guid sessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Sessions.GetValueOrDefault(sessionId));
        }

        public ValueTask<ProjectLearningSessionSnapshot?> LoadLatestCompletedInterviewAsync(
            string repositoryPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Sessions.Values
                .Where(session => session.Kind == LearningSessionKind.Interview &&
                    session.Status == LearningSessionStatus.Completed &&
                    string.Equals(session.RepositoryPath, repositoryPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static session => session.UpdatedAt)
                .FirstOrDefault());
        }
    }

    private sealed class FakeProfileRouter : IModelProfileRouter
    {
        public bool Fallback { get; set; }

        public int BeginCount { get; private set; }

        public int EndCount { get; private set; }

        public bool ThrowOnEnd { get; set; }

        public ValueTask<ModelProfileSelection> BeginSessionAsync(
            ModelProfile requestedProfile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCount++;
            return ValueTask.FromResult(new ModelProfileSelection(
                requestedProfile,
                Fallback ? ModelProfile.Fast : requestedProfile,
                Fallback,
                Fallback ? "deep_unavailable" : "selected"));
        }

        public ValueTask EndSessionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EndCount++;
            if (ThrowOnEnd)
            {
                throw new InvalidOperationException("Profile restoration failed.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
