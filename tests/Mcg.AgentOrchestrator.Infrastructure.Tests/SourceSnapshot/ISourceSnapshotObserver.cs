internal interface ISourceSnapshotObserver
{
    void OnEnumerated(string root);
    void OnTextRead(string root, string relativePath);
    void OnParsed(string root, string relativePath);
}
