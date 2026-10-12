namespace BestStories.Api.Services;

public sealed class StoryDataUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
