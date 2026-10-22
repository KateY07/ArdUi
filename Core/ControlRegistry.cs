namespace ArdUi;

// Which side of a session may answer a request: the controlled machine, the controlling one, or either.
enum ControlRole { Host, Controller, Either }

delegate Task<object> ControlHandler(string method, JsonElement data, CancellationToken ct);

// Everything the other side of a session may ask this side to do, in one table: the method (or a family of methods ending in '*'), which
// side answers it, and the permission it needs. A request that is not in the table, comes from the wrong direction or lacks the
// permission is refused here, before any handler runs.
sealed class ControlRegistry
{
    sealed record Entry(string Name, ControlRole Role, Perm Needs, ControlHandler Handle)
    {
        public bool Family => Name.EndsWith('*');
        public bool Matches(string method) => Family ? method.StartsWith(Name[..^1], StringComparison.Ordinal) : method == Name;
    }
    readonly List<Entry> entries = new();
    readonly bool host;
    readonly Func<Perm> perms;

    public ControlRegistry(bool host, Func<Perm> perms) { this.host = host; this.perms = perms; }

    public ControlRegistry Add(string name, ControlRole role, Perm needs, ControlHandler handle)
    {
        if (entries.Any(e => e.Name == name)) throw new InvalidOperationException("控制请求重复登记：" + name);
        entries.Add(new Entry(name, role, needs, handle)); return this;
    }

    public IEnumerable<(string Name, ControlRole Role, Perm Needs)> Table => entries.Select(e => (e.Name, e.Role, e.Needs));

    public Task<object> Dispatch(string method, JsonElement data, CancellationToken ct)
    {
        // an exact name wins over a family
        var entry = entries.FirstOrDefault(e => !e.Family && e.Matches(method)) ?? entries.FirstOrDefault(e => e.Family && e.Matches(method))
            ?? throw new IOException("对端不支持此请求，请更新两端 ArdUi。");
        if (entry.Role == ControlRole.Host && !host || entry.Role == ControlRole.Controller && host)
            throw new IOException("此请求的方向无效。");
        var missing = entry.Needs & ~perms();
        if (missing != Perm.None) throw new IOException("被控端未允许" + Permissions.Describe(missing) + "。");
        return entry.Handle(method, data, ct);
    }
}
