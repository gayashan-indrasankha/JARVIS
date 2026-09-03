using Jarvis.Core.ProjectLearning;

namespace Jarvis.Core.Tools;

public sealed record StartTutorSessionRequest(
    string RepositoryPath,
    TutorLevel Level = TutorLevel.Foundation,
    string Topic = "project overview",
    bool AskBeforeTell = false,
    ModelProfile Profile = ModelProfile.Fast) : IToolRequest;

public sealed record ContinueTutorSessionRequest(
    Guid SessionId,
    TutorInteractionKind Interaction,
    string UserInput) : IToolRequest;

public sealed record StartInterviewSessionRequest(
    string RepositoryPath,
    InterviewDifficulty Difficulty = InterviewDifficulty.Internship,
    int QuestionCount = 5,
    ModelProfile Profile = ModelProfile.Fast) : IToolRequest;

public sealed record SubmitInterviewAnswerRequest(
    Guid SessionId,
    string Answer) : IToolRequest;

public sealed record EndLearningSessionRequest(Guid SessionId) : IToolRequest;

public sealed record StartRevisionSessionRequest(
    string RepositoryPath,
    ModelProfile Profile = ModelProfile.Fast) : IToolRequest;

public sealed record ProjectLearningResponse(ProjectLearningTurnResult Result) : IToolResponse;
