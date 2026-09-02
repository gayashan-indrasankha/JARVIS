using Jarvis.Core.ProjectIntelligence;

namespace Jarvis.Core.ProjectLearning;

public static class ProjectLearningLimits
{
    public const int MaximumTopicCharacters = 256;
    public const int MaximumAnswerCharacters = 8_192;
    public const int MaximumResponseCharacters = 4_096;
    public const int MaximumConcepts = 16;
    public const int MaximumTurns = 32;
    public const int MaximumEvidenceItems = 12;
    public const int MaximumTurnEvidenceItems = 6;
}

public enum ModelProfile
{
    Fast,
    Deep,
}

public enum LearningSessionKind
{
    Tutor,
    Interview,
}

public enum LearningSessionStatus
{
    Active,
    Completed,
    Cancelled,
}

public enum TutorLevel
{
    Foundation,
    Architecture,
    FeatureFlow,
    Implementation,
    Database,
    Security,
    Testing,
    FailureHandling,
    Scalability,
    TradeOffs,
    InterviewDefence,
}

public enum TutorInteractionKind
{
    Explain,
    GoDeeper,
    AskQuestion,
    SelfExplanation,
    ShowEvidence,
    Recap,
}

public enum InterviewDifficulty
{
    Internship,
    Junior,
    MidLevelStretch,
}

public enum InterviewDimension
{
    ProjectOverview,
    Architecture,
    ActualImplementation,
    CSharpDotNet,
    ApiDesign,
    Database,
    Security,
    Testing,
    ErrorHandling,
    Performance,
    Concurrency,
    FailureScenarios,
    Scalability,
    DesignTradeOffs,
}

public enum ScoreDimension
{
    ProjectFactualAccuracy,
    TechnicalDepth,
    Reasoning,
    TradeOffAwareness,
    CSharpDotNetUnderstanding,
    DatabaseUnderstanding,
    TestingUnderstanding,
    SecurityAwareness,
    CommunicationClarity,
    ConfidenceCalibration,
}

public enum LearningStatementKind
{
    ProjectFact,
    GeneralPrinciple,
    DesignAlternative,
}

public sealed record ModelProfileSelection(
    ModelProfile Requested,
    ModelProfile Selected,
    bool FellBack,
    string ReasonCode);

public sealed record LearningStatement(
    LearningStatementKind Kind,
    string Text,
    IReadOnlyList<ProjectEvidence> Evidence);

public sealed record TutorTurn(
    int Sequence,
    TutorLevel Level,
    TutorInteractionKind Interaction,
    string Explanation,
    string? SocraticQuestion,
    IReadOnlyList<string> ExpectedConcepts,
    IReadOnlyList<LearningStatement> Statements,
    IReadOnlyList<string> ObservedStrengths,
    IReadOnlyList<string> ObservedGaps,
    ProjectContextBudget ContextBudget,
    DateTimeOffset CreatedAt);

public sealed record InterviewQuestion(
    Guid QuestionId,
    int Sequence,
    InterviewDimension Dimension,
    string Text,
    IReadOnlyList<string> ExpectedConcepts,
    IReadOnlyList<ProjectEvidence> Evidence,
    bool IsFollowUp,
    Guid? ParentQuestionId);

public sealed record DimensionScore(
    ScoreDimension Dimension,
    int Score,
    string Rubric,
    string Rationale);

public sealed record InterviewEvaluation(
    Guid QuestionId,
    IReadOnlyList<DimensionScore> Scores,
    double OverallScore,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Gaps,
    string Rationale,
    IReadOnlyList<LearningStatement> Corrections,
    bool RequiresTargetedFollowUp);

public sealed record InterviewTurn(
    InterviewQuestion Question,
    string UserAnswer,
    InterviewEvaluation Evaluation,
    DateTimeOffset AnsweredAt);

public sealed record LearningTranscriptEntry(
    int Sequence,
    string Speaker,
    string Text,
    DateTimeOffset CreatedAt);

public sealed record LearningReportCategory(string Name, double Score);

public sealed record ProjectLearningReport(
    Guid SessionId,
    IReadOnlyList<LearningReportCategory> Categories,
    IReadOnlyList<string> StrongAreas,
    IReadOnlyList<string> WeakAreas,
    IReadOnlyList<string> PoorlyAnsweredQuestions,
    IReadOnlyList<string> RevisionTopics,
    InterviewDifficulty SuggestedNextDifficulty,
    DateTimeOffset CompletedAt);

public sealed record ProjectLearningSessionSnapshot(
    Guid SessionId,
    LearningSessionKind Kind,
    LearningSessionStatus Status,
    string RepositoryPath,
    string RepositorySnapshotId,
    ModelProfile RequestedProfile,
    ModelProfile SelectedProfile,
    bool ProfileFellBack,
    string ProfileReasonCode,
    TutorLevel? TutorLevel,
    bool AskBeforeTell,
    InterviewDifficulty? InterviewDifficulty,
    int TargetQuestionCount,
    IReadOnlyList<TutorTurn> TutorTurns,
    IReadOnlyList<InterviewTurn> InterviewTurns,
    InterviewQuestion? CurrentQuestion,
    IReadOnlyList<LearningTranscriptEntry> Transcript,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Gaps,
    ProjectLearningReport? Report,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProjectLearningTurnResult(
    Guid SessionId,
    LearningSessionKind Kind,
    LearningSessionStatus Status,
    ModelProfileSelection Profile,
    TutorTurn? TutorTurn,
    InterviewQuestion? InterviewQuestion,
    InterviewEvaluation? Evaluation,
    ProjectLearningReport? Report,
    bool ReadyToComplete);

public sealed record TutorGenerationRequest(
    TutorLevel Level,
    TutorInteractionKind Interaction,
    string Topic,
    string? UserExplanation,
    bool AskBeforeTell,
    IReadOnlyList<ProjectClaim> EvidenceClaims,
    IReadOnlyList<TutorTurn> RecentTurns,
    int MaximumResponseCharacters);

public sealed record TutorGenerationOutput(
    string Explanation,
    string? SocraticQuestion,
    IReadOnlyList<string> ExpectedConcepts,
    IReadOnlyList<int> EvidenceIndexes,
    IReadOnlyList<string> ObservedStrengths,
    IReadOnlyList<string> ObservedGaps,
    IReadOnlyList<string>? DesignAlternatives = null);

public sealed record InterviewQuestionGenerationRequest(
    InterviewDifficulty Difficulty,
    InterviewDimension Dimension,
    int Sequence,
    bool IsFollowUp,
    IReadOnlyList<string> TargetGaps,
    IReadOnlyList<ProjectClaim> EvidenceClaims,
    IReadOnlyList<InterviewTurn> RecentTurns,
    int MaximumResponseCharacters);

public sealed record InterviewQuestionGenerationOutput(
    string Question,
    IReadOnlyList<string> ExpectedConcepts,
    IReadOnlyList<int> EvidenceIndexes);

public sealed record InterviewAnswerAssessmentRequest(
    InterviewDifficulty Difficulty,
    InterviewQuestion Question,
    string UserAnswer,
    IReadOnlyList<ProjectClaim> EvidenceClaims);

public sealed record InterviewAnswerAssessmentOutput(
    IReadOnlyList<int> DemonstratedConceptIndexes,
    IReadOnlyList<int> IncorrectConceptIndexes,
    bool ShowsReasoning,
    bool ShowsTradeOffAwareness,
    bool ShowsCSharpDotNetUnderstanding,
    bool ShowsDatabaseUnderstanding,
    bool ShowsTestingUnderstanding,
    bool ShowsSecurityAwareness,
    int CommunicationClarity,
    int ConfidenceCalibration,
    string Rationale,
    string? Correction,
    IReadOnlyList<int> CorrectionEvidenceIndexes);

public interface IProjectLearningModel
{
    public ValueTask<TutorGenerationOutput> GenerateTutorTurnAsync(
        TutorGenerationRequest request,
        CancellationToken cancellationToken);

    public ValueTask<InterviewQuestionGenerationOutput> GenerateInterviewQuestionAsync(
        InterviewQuestionGenerationRequest request,
        CancellationToken cancellationToken);

    public ValueTask<InterviewAnswerAssessmentOutput> AssessInterviewAnswerAsync(
        InterviewAnswerAssessmentRequest request,
        CancellationToken cancellationToken);
}

public interface IProjectLearningEvidenceSource
{
    public ValueTask<GroundedProjectAnswer> GetTutorEvidenceAsync(
        string repositoryPath,
        TutorLevel level,
        string topic,
        CancellationToken cancellationToken);

    public ValueTask<GroundedProjectAnswer> GetInterviewEvidenceAsync(
        string repositoryPath,
        InterviewDimension dimension,
        CancellationToken cancellationToken);
}

public interface IModelProfileRouter
{
    public ValueTask<ModelProfileSelection> BeginSessionAsync(
        ModelProfile requestedProfile,
        CancellationToken cancellationToken);

    public ValueTask EndSessionAsync(CancellationToken cancellationToken);
}

public interface IProjectLearningSessionStore
{
    public ValueTask SaveAsync(
        ProjectLearningSessionSnapshot session,
        CancellationToken cancellationToken);

    public ValueTask<ProjectLearningSessionSnapshot?> LoadAsync(
        Guid sessionId,
        CancellationToken cancellationToken);

    public ValueTask<ProjectLearningSessionSnapshot?> LoadLatestCompletedInterviewAsync(
        string repositoryPath,
        CancellationToken cancellationToken);
}

public interface IProjectLearningService
{
    public ValueTask<ProjectLearningTurnResult> StartTutorAsync(
        string repositoryPath,
        TutorLevel level,
        string topic,
        bool askBeforeTell,
        ModelProfile requestedProfile,
        CancellationToken cancellationToken);

    public ValueTask<ProjectLearningTurnResult> ContinueTutorAsync(
        Guid sessionId,
        TutorInteractionKind interaction,
        string userInput,
        CancellationToken cancellationToken);

    public ValueTask<ProjectLearningTurnResult> StartInterviewAsync(
        string repositoryPath,
        InterviewDifficulty difficulty,
        int questionCount,
        ModelProfile requestedProfile,
        CancellationToken cancellationToken);

    public ValueTask<ProjectLearningTurnResult> SubmitInterviewAnswerAsync(
        Guid sessionId,
        string answer,
        CancellationToken cancellationToken);

    public ValueTask<ProjectLearningTurnResult> EndSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken);

    public ValueTask<ProjectLearningTurnResult> StartRevisionAsync(
        string repositoryPath,
        ModelProfile requestedProfile,
        CancellationToken cancellationToken);
}

public sealed record ProjectLearningConfiguration(
    int MaximumContextCharacters = 12_000,
    int MaximumEvidenceItems = 10,
    int MaximumRecentTurns = 6,
    int MinimumInterviewQuestions = 5,
    int MaximumInterviewQuestions = 20)
{
    public ProjectLearningConfiguration() : this(12_000, 10, 6, 5, 20)
    {
    }
}

public sealed class ProjectLearningException : InvalidOperationException
{
    public ProjectLearningException(string code)
        : base("Project learning could not complete safely.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}
