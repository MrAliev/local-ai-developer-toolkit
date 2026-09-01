namespace LocalAi.Cli;

public enum RepositoryHookEvent
{
    PostCommit,
    PostMerge,
    PostRewrite,
    PostCheckout,
    ReferenceTransaction
}
