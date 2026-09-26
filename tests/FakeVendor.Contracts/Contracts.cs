// Original, deliberately small test doubles. These are NOT Microsoft's code
// or DLLs. Names/signatures model only the contract exercised by our adapter.
namespace YourPhone.AppCore.WinRT.DataStore;

public interface ISqliteConnection : IDisposable
{
    string FileName { get; }
    ISqliteStatement CreateStatement(string sql);
    void AddTable(object table);
    void RemoveTable(object table);
    object CreateTransaction(); // must be rejected by the read-only adapter
}

public interface IFixtureBlobReference
{
    Task<Stream> OpenReadAsync();
}

public interface ISqliteStatement : IDisposable
{
    string Statement { get; }
    bool Step();
    void Reset();
    void BindInt(int index, int value);
    void BindInt64(int index, long value);
    void BindBool(int index, bool value);
    void BindDouble(int index, double value);
    void BindDateTime(int index, DateTimeOffset value);
    void BindText16(int index, string? value);
    void BindText(int index, byte[] value);
    void BindNull(int index);
    bool HasColumn(string name);
    bool IsDbNull(string name);
    int ReadInt(string name);
    long ReadInt64(string name);
    double ReadDouble(string name);
    bool ReadBool(string name);
    DateTimeOffset ReadDateTime(string name);
    string ReadText16(string name);
    IFixtureBlobReference? ReadBlob(string column, string table, long id);
}
