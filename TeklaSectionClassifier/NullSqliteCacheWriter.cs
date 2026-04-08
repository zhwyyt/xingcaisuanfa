namespace TeklaSectionClassifier;

public sealed class NullSqliteCacheWriter : ISqliteCacheWriter
{
    public void InitializeDatabase(string databasePath)
    {
    }

    public void UpsertMember(MemberSnapshot member, string databasePath)
    {
    }
}
