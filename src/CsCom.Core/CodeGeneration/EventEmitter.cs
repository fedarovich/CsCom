using System.Globalization;
using System.Runtime.InteropServices;
using CsCom.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CsCom.CodeGeneration;

/// <summary>
/// Emits the code that connects to the events of the event (source) interfaces of coclasses: a handler interface with a method
/// per event and a sink class that raises the events as .NET events or pushes them to observables, see <see cref="EventStyle"/>.
/// </summary>
/// <remarks>
/// Dispinterface events are received by <c>DispatchEventSink</c> of the support code, which passes the invocations of
/// <c>IDispatch::Invoke</c> to a generated <c>DispatchHandler</c>. Vtable and dual event interfaces are implemented by a
/// generated <c>[GeneratedComClass]</c> sink, which also implements <c>IDispatch::Invoke</c> for dual interfaces.
/// </remarks>
internal sealed partial class EventEmitter
{
    private const string NotImplemented = "unchecked((int)0x80004001)";
    private const string UnknownName = "unchecked((int)0x80020006)";

    // The members of the sink classes and of object, which the events must not hide.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "Iid", "Connect", "Dispose", "DispatchHandler", "ComSink", "Equals", "GetHashCode", "GetType", "ToString", "MemberwiseClone", "ReferenceEquals", "Finalize",
    };

    private readonly EmitContext _context;
    private readonly HashSet<InterfaceDefinition> _sources;

    public EventEmitter(EmitContext context)
    {
        _context = context;
        _sources = context.Options.EventStyle == EventStyle.None
            ? []
            : context.Library.Types.OfType<CoClassDefinition>()
                .SelectMany(static c => c.Interfaces)
                .Where(static i => i.IsSource)
                .Select(static i => i.Interface)
                .OfType<UserDefinedTypeReference>()
                .Select(static r => r.Definition)
                .OfType<InterfaceDefinition>()
                .ToHashSet();
    }

    /// <summary>
    /// Emits the event code for the interface, if it is the event interface of a coclass.
    /// </summary>
    public IEnumerable<MemberDeclarationSyntax> Emit(InterfaceDefinition definition, string @namespace)
    {
        if (!_sources.Contains(definition))
        {
            return [];
        }

        if (definition.Guid is not { } guid)
        {
            LogMissingGuid(_context.Logger, definition.Name);
            return [];
        }

        var signatures = new SignatureBuilder(_context, new TypeMapper(_context, @namespace), @namespace);
        var source = definition.InterfaceKind == InterfaceKind.Dispatch
            ? CreateDispatchSource(definition, guid, signatures)
            : CreateVtableSource(definition, guid, signatures, _context.Options.Marshalling);

        var members = new List<MemberDeclarationSyntax> { EmitHandlerInterface(source) };
        switch (_context.Options.EventStyle)
        {
            case EventStyle.Events:
                members.AddRange(source.Events.Select(e => EmitDelegate(source, e)));
                break;
            case EventStyle.Observable:
                members.AddRange(source.Events.Select(e => EmitEventArgs(source, e)));
                break;
        }

        members.Add(EmitSink(source));
        return members;
    }

    private static Source CreateDispatchSource(InterfaceDefinition definition, Guid guid, SignatureBuilder signatures)
    {
        var events = new List<Event>();
        foreach (var method in definition.Methods.Where(static m => m.InvokeKind == InvokeKind.Method))
        {
            var (returnType, parameters) = signatures.MapLateBoundSignature(method);
            events.Add(new Event(method, method.Name, returnType.Name, parameters, IsDispatched: true));
        }

        return new Source(definition, guid, AssignNames(definition, events), [], IsDispatch: true, IsDual: false);
    }

    private static Source CreateVtableSource(InterfaceDefinition definition, Guid guid, SignatureBuilder signatures, MarshallingOptions marshalling)
    {
        // The sink implements the methods of the base interfaces as well.
        var hierarchy = new List<InterfaceDefinition>();
        bool isDual = false;
        for (InterfaceDefinition? current = definition; current is not null;)
        {
            hierarchy.Insert(0, current);
            switch (current.BaseInterface)
            {
                case UserDefinedTypeReference { Definition: InterfaceDefinition { InterfaceKind: not InterfaceKind.Dispatch } baseInterface }:
                    current = baseInterface;
                    break;
                case UserDefinedTypeReference or PrimitiveTypeReference { VarType: VarEnum.VT_DISPATCH }:
                    isDual = true;
                    current = null;
                    break;
                default:
                    current = null;
                    break;
            }
        }

        var events = new List<Event>();
        var dispIds = new HashSet<int>();
        foreach (var method in hierarchy.SelectMany(static i => i.Methods))
        {
            var (returnType, parameters) = signatures.MapComSignature(method);
            var @event = new Event(method, SignatureBuilder.GetMethodName(method), returnType.Name, parameters, IsDispatched: false);

            // Dual interfaces are also invoked through IDispatch::Invoke, which cannot pass pointers. It passes SAFEARRAYs and
            // BSTRs in VARIANTs, which are converted to .NET arrays and strings rather than to the wrappers or the raw pointers,
            // so these events are only received through the vtable.
            bool needsVtable = method.Parameters.Any(p => (TypeMapper.TryGetByRefElement(p.Type, out var element) ? element : p.Type) switch
            {
                SafeArrayTypeReference => marshalling.SafeArrays is SafeArrayMarshalling.Wrapper or SafeArrayMarshalling.None,
                PrimitiveTypeReference { VarType: VarEnum.VT_BSTR } => marshalling.Bstrs is BstrMarshalling.Wrapper or BstrMarshalling.None,
                _ => false,
            });
            @event = @event with
            {
                IsDispatched = isDual && method.InvokeKind == InvokeKind.Method && !@event.HasPointers && !needsVtable && dispIds.Add(method.MemberId),
            };
            events.Add(@event);
        }

        var gaps = hierarchy.SelectMany(InterfaceEmitter.GetVtableGaps).ToList();
        return new Source(definition, guid, AssignNames(definition, events), gaps, IsDispatch: false, IsDual: isDual);
    }

    // Makes the names of the handler methods unique and keeps the events from hiding the members of the sink class.
    private static List<Event> AssignNames(InterfaceDefinition definition, List<Event> events)
    {
        var handlerNames = new HashSet<string>(StringComparer.Ordinal);
        var eventNames = new HashSet<string>(ReservedNames, StringComparer.Ordinal) { definition.Name + "Sink" };
        var result = new List<Event>(events.Count);
        foreach (var @event in events)
        {
            string handlerName = Unique(@event.HandlerName, handlerNames);
            string eventName = handlerName;
            if (eventNames.Contains(eventName))
            {
                eventName += "Event";
            }

            result.Add(@event with { HandlerName = handlerName, Name = Unique(eventName, eventNames) });
        }

        return result;

        static string Unique(string name, HashSet<string> names)
        {
            string unique = name;
            for (int i = 2; !names.Add(unique); i++)
            {
                unique = $"{name}_{i.ToString(CultureInfo.InvariantCulture)}";
            }

            return unique;
        }
    }

    private InterfaceDeclarationSyntax EmitHandlerInterface(Source source)
    {
        var members = source.Events.Select(e =>
        {
            var outs = e.Parameters.Where(static p => p.Modifier == SyntaxKind.OutKeyword).ToList();
            string body = outs.Count == 0
                ? e.IsVoid ? "{ }" : "=> default;"
                : $"{{ {string.Concat(outs.Select(static p => $"{p.Name} = default; "))}{(e.IsVoid ? string.Empty : "return default;")} }}";
            return ParseMember($"{e.ReturnType} {Syntax.Escape(e.HandlerName)}({Parameters(e)}) {body}").WithDocumentation(e.Method.Documentation);
        });

        return InterfaceDeclaration(Syntax.Identifier(source.HandlerName))
            .WithModifiers(Modifiers(source.IsUnsafe, SyntaxKind.PartialKeyword))
            .WithMembers(List(members))
            .WithDocumentation($"Handles the events of the {source.Definition.OriginalName} event interface. The methods do nothing by default, so only the events of interest have to be implemented.");
    }

    private MemberDeclarationSyntax EmitDelegate(Source source, Event @event)
    {
        string modifiers = Accessibility + (@event.HasPointers ? " unsafe" : string.Empty);
        return ParseMember($"{modifiers} delegate {@event.ReturnType} {source.DelegateName(@event)}({Parameters(@event)});")
            .WithDocumentation($"Represents the method that handles the {@event.Method.OriginalName} event of the {source.Definition.OriginalName} event interface.");
    }

    private ClassDeclarationSyntax EmitEventArgs(Source source, Event @event)
    {
        var members = new List<MemberDeclarationSyntax>();
        foreach (var parameter in @event.Parameters)
        {
            string accessor = parameter.IsByRef ? "set" : "init";
            members.Add(ParseMember($"public {parameter.TypeName} {PropertyName(@event, parameter)} {{ get; {accessor}; }}").WithDocumentation(parameter.IsByRef
                ? $"Gets or sets the {parameter.Definition.Name} argument, which is passed back to the COM object."
                : $"Gets the {parameter.Definition.Name} argument."));
        }

        if (!@event.IsVoid)
        {
            members.Add(ParseMember($"public {@event.ReturnType} Result {{ get; set; }}")
                .WithDocumentation("Gets or sets the result of the event, which is passed back to the COM object."));
        }

        return ClassDeclaration(Syntax.Identifier(source.EventArgsName(@event)))
            .WithModifiers(Modifiers(@event.HasPointers, SyntaxKind.SealedKeyword))
            .WithMembers(List(members))
            .WithDocumentation($"Provides data for the {@event.Method.OriginalName} event of the {source.Definition.OriginalName} event interface.");
    }

    private ClassDeclarationSyntax EmitSink(Source source)
    {
        string support = _context.Support;
        string name = source.Definition.OriginalName;
        var style = _context.Options.EventStyle;
        var members = new List<MemberDeclarationSyntax>
        {
            ParseMember($"public static readonly global::System.Guid Iid = new({Syntax.GuidLiteral(source.Guid)});")
                .WithDocumentation($"The interface identifier (IID) of the {name} event interface."),
        };

        if (style != EventStyle.Interface)
        {
            members.Add(ParseMember($"private readonly {support}.EventConnection __connection;"));
            if (style == EventStyle.Observable)
            {
                members.AddRange(source.Events.Select(e => ParseMember($"private readonly {support}.EventSubject<{source.EventArgsName(e)}> __{e.Name} = new();")));
            }

            members.Add(ParseMember($"public {source.SinkName}(object source) {{ __connection = Connect(source, this); }}").WithDocumentation(
                $"Connects the sink to the {name} events of a COM object.",
                ("param name=\"source\"", "The COM object that raises the events.")));

            foreach (var @event in source.Events)
            {
                string documentation = @event.Method.Documentation ?? $"Occurs when the COM object raises the {@event.Method.OriginalName} event.";
                members.Add(style == EventStyle.Events
                    ? ParseMember($"public event {source.DelegateName(@event)}? {Syntax.Escape(@event.Name)};").WithDocumentation(documentation)
                    : ParseMember($"public global::System.IObservable<{source.EventArgsName(@event)}> {Syntax.Escape(@event.Name)} => __{@event.Name};").WithDocumentation(documentation));
            }

            string completions = style == EventStyle.Observable ? string.Concat(source.Events.Select(static e => $"__{e.Name}.OnCompleted(); ")) : string.Empty;
            members.Add(ParseMember($"public void Dispose() {{ __connection.Dispose(); {completions}}}").WithDocumentation(style == EventStyle.Observable
                ? "Disconnects the sink from the events and completes the observables."
                : "Disconnects the sink from the events."));
        }

        string connect = source.IsDispatch
            ? $"{support}.EventConnection.AdviseDispatch(source, Iid, new DispatchHandler(handler))"
            : $"{support}.EventConnection.Advise(source, Iid, new ComSink(handler))";
        members.Add(ParseMember($"public static {support}.EventConnection Connect(object source, {source.HandlerName} handler) {{ global::System.ArgumentNullException.ThrowIfNull(handler); return {connect}; }}")
            .WithDocumentation(
                $"Connects a handler to the {name} events of a COM object.",
                ("param name=\"source\"", "The COM object that raises the events."),
                ("param name=\"handler\"", "The handler of the events."),
                ("returns", "The connection, which must be disposed to disconnect the handler.")));

        if (style == EventStyle.Events)
        {
            members.AddRange(source.Events.Select(e => RaiseEvent(source, e)));
        }
        else if (style == EventStyle.Observable)
        {
            members.AddRange(source.Events.Select(e => PushEvent(source, e)));
        }

        if (source.IsDispatch || source.IsDual)
        {
            members.Add(EmitDispatchHandler(source));
        }

        if (!source.IsDispatch)
        {
            members.Add(EmitComSink(source));
        }

        string summary = style switch
        {
            EventStyle.Events => $"Connects to the {name} events of a COM object and raises them as .NET events. Dispose the sink to disconnect it.",
            EventStyle.Observable => $"Connects to the {name} events of a COM object and pushes them to observables. Dispose the sink to disconnect it.",
            _ => $"Connects handlers to the {name} events of COM objects.",
        };

        var declaration = ClassDeclaration(Syntax.Identifier(source.SinkName));
        if (style == EventStyle.Interface)
        {
            declaration = declaration.WithModifiers(Syntax.Modifiers(_context.Accessibility, SyntaxKind.StaticKeyword, SyntaxKind.PartialKeyword));
        }
        else
        {
            var baseTypes = new[] { source.HandlerName, "global::System.IDisposable" }.Select(static t => (BaseTypeSyntax)SimpleBaseType(Syntax.Type(t)));
            declaration = declaration
                .WithModifiers(Modifiers(source.IsUnsafe, SyntaxKind.SealedKeyword, SyntaxKind.PartialKeyword))
                .WithBaseList(BaseList(SeparatedList(baseTypes)));
        }

        return declaration.WithMembers(List(members)).WithDocumentation(summary);
    }

    // The explicit implementation of a handler method that raises the .NET event.
    private static MemberDeclarationSyntax RaiseEvent(Source source, Event @event)
    {
        string signature = $"{@event.ReturnType} {source.HandlerName}.{Syntax.Escape(@event.HandlerName)}({Parameters(@event)})";
        string field = $"this.{Syntax.Escape(@event.Name)}";
        string arguments = Arguments(@event.Parameters);
        var outs = @event.Parameters.Where(static p => p.Modifier == SyntaxKind.OutKeyword).ToList();
        if (outs.Count == 0)
        {
            return ParseMember(@event.IsVoid
                ? $"{signature} => {field}?.Invoke({arguments});"
                : $"{signature} => {field} is {{ }} __handler ? __handler({arguments}) : default;");
        }

        string call = @event.IsVoid ? $"__handler({arguments}); return;" : $"return __handler({arguments});";
        string defaults = string.Concat(outs.Select(static p => $"{p.Name} = default; "));
        return ParseMember($"{signature} {{ if ({field} is {{ }} __handler) {{ {call} }} {defaults}{(@event.IsVoid ? string.Empty : "return default;")} }}");
    }

    // The explicit implementation of a handler method that pushes the event arguments to the observable.
    private static MemberDeclarationSyntax PushEvent(Source source, Event @event)
    {
        string signature = $"{@event.ReturnType} {source.HandlerName}.{Syntax.Escape(@event.HandlerName)}({Parameters(@event)})";
        string initializers = string.Join(", ", @event.Parameters
            .Where(static p => p.Modifier != SyntaxKind.OutKeyword)
            .Select(p => $"{PropertyName(@event, p)} = {p.Name}"));
        string create = $"new {source.EventArgsName(@event)} {{ {initializers} }}";
        var byRef = @event.Parameters.Where(static p => p.IsByRef).ToList();
        if (byRef.Count == 0 && @event.IsVoid)
        {
            return ParseMember($"{signature} => this.__{@event.Name}.OnNext({create});");
        }

        string results = string.Concat(byRef.Select(p => $"{p.Name} = __args.{PropertyName(@event, p)}; "));
        return ParseMember($"{signature} {{ var __args = {create}; this.__{@event.Name}.OnNext(__args); {results}{(@event.IsVoid ? string.Empty : "return __args.Result;")} }}");
    }

    // Passes the invocations of IDispatch::Invoke to the handler.
    private ClassDeclarationSyntax EmitDispatchHandler(Source source)
    {
        string support = _context.Support;
        var sections = new List<SwitchSectionSyntax>();
        foreach (var @event in source.Events.Where(static e => e.IsDispatched))
        {
            var statements = new List<string>();
            var arguments = new List<string>();
            for (int i = 0; i < @event.Parameters.Count; i++)
            {
                var parameter = @event.Parameters[i];
                string value = ReadArgument(parameter, i);
                switch (parameter.Modifier)
                {
                    case SyntaxKind.RefKeyword:
                        statements.Add($"var __a{i} = {value};");
                        arguments.Add($"ref __a{i}");
                        break;
                    case SyntaxKind.OutKeyword:
                        arguments.Add($"out var __a{i}");
                        break;
                    default:
                        arguments.Add(value);
                        break;
                }
            }

            string call = $"__handler.{Syntax.Escape(@event.HandlerName)}({string.Join(", ", arguments)})";
            statements.Add(@event.IsVoid ? $"{call};" : $"var __result = {call};");
            for (int i = 0; i < @event.Parameters.Count; i++)
            {
                if (@event.Parameters[i].IsByRef)
                {
                    statements.Add($"arguments.Set({i.ToString(CultureInfo.InvariantCulture)}, __a{i});");
                }
            }

            if (!@event.IsVoid)
            {
                statements.Add("arguments.SetResult(__result);");
            }

            statements.Add("return true;");
            sections.Add(SwitchSection(
                SingletonList<SwitchLabelSyntax>(CaseSwitchLabel(Syntax.Expression(@event.Method.MemberId.ToString(CultureInfo.InvariantCulture)))),
                SingletonList<StatementSyntax>(Block(statements.Select(Syntax.Statement)))));
        }

        sections.Add(SwitchSection(SingletonList<SwitchLabelSyntax>(DefaultSwitchLabel()), SingletonList(Syntax.Statement("return false;"))));
        var invoke = MethodDeclaration(Syntax.Type("bool"), Identifier("Invoke"))
            .WithModifiers(Syntax.Modifiers(SyntaxKind.PublicKeyword))
            .WithParameterList(Syntax.Parameters([Syntax.Parameter("int", "dispId"), Syntax.Parameter($"{support}.EventArguments", "arguments")]))
            .WithBody(Block(SwitchStatement(IdentifierName("dispId")).WithSections(List(sections))));

        return ClassDeclaration(Identifier("DispatchHandler"))
            .WithModifiers(Syntax.Modifiers(SyntaxKind.PrivateKeyword, SyntaxKind.SealedKeyword))
            .WithBaseList(BaseList(SingletonSeparatedList<BaseTypeSyntax>(SimpleBaseType(Syntax.Type($"{support}.IDispatchEventHandler")))))
            .WithMembers(List(
            [
                ParseMember($"private readonly {source.HandlerName} __handler;"),
                ParseMember($"public DispatchHandler({source.HandlerName} handler) {{ __handler = handler; }}"),
                invoke,
            ]))
            .WithDocumentation("Passes the events raised through IDispatch::Invoke to the handler.");
    }

    // Implements the vtable (and dual) event interface for the connection point.
    private ClassDeclarationSyntax EmitComSink(Source source)
    {
        string support = _context.Support;
        var members = new List<MemberDeclarationSyntax>
        {
            ParseMember($"private readonly {source.HandlerName} __handler;"),
        };

        if (source.IsDual)
        {
            members.Add(ParseMember("private readonly DispatchHandler __dispatch;"));
            members.Add(ParseMember($"public ComSink({source.HandlerName} handler) {{ __handler = handler; __dispatch = new DispatchHandler(handler); }}"));
        }
        else
        {
            members.Add(ParseMember($"public ComSink({source.HandlerName} handler) {{ __handler = handler; }}"));
        }

        foreach (var @event in source.Events)
        {
            members.Add(ParseMember(
                $"public {@event.ReturnType} {Syntax.Escape(SignatureBuilder.GetMethodName(@event.Method))}({Parameters(@event)}) => __handler.{Syntax.Escape(@event.HandlerName)}({Arguments(@event.Parameters)});"));
        }

        members.AddRange(source.VtableGaps.Select(static slot => ParseMember($"public int {InterfaceEmitter.GetPlaceholderName(slot)}() => {NotImplemented};")));

        if (source.IsDual)
        {
            string dispatch = $"{support}.IDispatch";
            members.Add(ParseMember($"int {dispatch}.GetTypeInfoCount(out uint pctinfo) {{ pctinfo = 0; return 0; }}"));
            members.Add(ParseMember($"int {dispatch}.GetTypeInfo(uint iTInfo, uint lcid, out nint ppTInfo) {{ ppTInfo = 0; return {NotImplemented}; }}"));
            members.Add(ParseMember(
                $"int {dispatch}.GetIDsOfNames(in global::System.Guid riid, char** rgszNames, uint cNames, uint lcid, int* rgDispId) {{ for (uint i = 0; i < cNames; i++) {{ rgDispId[i] = -1; }} return {UnknownName}; }}"));
            members.Add(ParseMember(
                $"int {dispatch}.Invoke(int dispIdMember, in global::System.Guid riid, uint lcid, ushort wFlags, {support}.DispParams* pDispParams, {Syntax.MarshallingNamespace}.ComVariant* pVarResult, {support}.ExcepInfo* pExcepInfo, uint* puArgErr) => " +
                $"{support}.DispatchEventSink.InvokeHandler(__dispatch, dispIdMember, pDispParams, pVarResult, pExcepInfo);"));
        }

        var modifiers = new List<SyntaxKind> { SyntaxKind.PrivateKeyword, SyntaxKind.SealedKeyword };
        if (source.IsDual || source.IsUnsafe)
        {
            modifiers.Add(SyntaxKind.UnsafeKeyword);
        }

        modifiers.Add(SyntaxKind.PartialKeyword);
        return ClassDeclaration(Identifier("ComSink"))
            .WithModifiers(Syntax.Modifiers([.. modifiers]))
            .WithAttributeLists(SingletonList(Syntax.AttributeList(Syntax.Attribute($"{Syntax.MarshallingNamespace}.GeneratedComClass"))))
            .WithBaseList(BaseList(SingletonSeparatedList<BaseTypeSyntax>(SimpleBaseType(Syntax.Type(source.InterfaceName)))))
            .WithMembers(List(members))
            .WithDocumentation($"Implements the {source.Definition.OriginalName} event interface and passes the events to the handler.");
    }

    private static string ReadArgument(SignatureParameter parameter, int index)
    {
        string position = index.ToString(CultureInfo.InvariantCulture);
        if (IsDispatchWrapper(parameter.ValueType))
        {
            return $"arguments.GetWrapper<{parameter.TypeName.TrimEnd('?')}>({position})";
        }

        return parameter.TypeName.EndsWith("[]?", StringComparison.Ordinal)
            ? $"arguments.GetArray<{parameter.TypeName[..^3]}>({position})"
            : $"arguments.Get<{parameter.TypeName}>({position})";
    }

    private static bool IsDispatchWrapper(TypeReference type) => type switch
    {
        UserDefinedTypeReference { Definition: InterfaceDefinition { InterfaceKind: InterfaceKind.Dispatch } } => true,
        UserDefinedTypeReference { Definition: AliasDefinition alias } => IsDispatchWrapper(alias.Target),
        UserDefinedTypeReference { Definition: CoClassDefinition coClass } => coClass.DefaultInterface?.Interface is { } defaultInterface && IsDispatchWrapper(defaultInterface),
        PointerTypeReference pointer => TypeMapper.IsInterfaceLike(pointer.ElementType) && IsDispatchWrapper(pointer.ElementType),
        _ => false,
    };

    private static string Parameters(Event @event) =>
        string.Join(", ", @event.Parameters.Select(static p => $"{Modifier(p)}{p.TypeName} {p.Name}"));

    private static string Arguments(IEnumerable<SignatureParameter> parameters) =>
        string.Join(", ", parameters.Select(static p => $"{Modifier(p)}{p.Name}"));

    private static string Modifier(SignatureParameter parameter) => parameter.Modifier switch
    {
        SyntaxKind.RefKeyword => "ref ",
        SyntaxKind.OutKeyword => "out ",
        SyntaxKind.InKeyword => "in ",
        _ => string.Empty,
    };

    private static string PropertyName(Event @event, SignatureParameter parameter) =>
        !@event.IsVoid && parameter.Definition.Name == "Result" ? "Result_" : parameter.Name;

    private string Accessibility => _context.Accessibility == SyntaxKind.InternalKeyword ? "internal" : "public";

    private SyntaxTokenList Modifiers(bool isUnsafe, params SyntaxKind[] modifiers) =>
        Syntax.Modifiers([_context.Accessibility, .. isUnsafe ? [SyntaxKind.UnsafeKeyword] : Array.Empty<SyntaxKind>(), .. modifiers]);

    private static MemberDeclarationSyntax ParseMember(string text)
    {
        var member = ParseMemberDeclaration(text, options: Syntax.ParseOptions);
        if (member is null || member.ContainsDiagnostics)
        {
            throw new InvalidOperationException($"Invalid member generated: {text}");
        }

        return member;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event interface '{Name}': skipping the events, because the interface has no GUID")]
    private static partial void LogMissingGuid(ILogger logger, string name);

    /// <summary>
    /// An event of an event interface.
    /// </summary>
    /// <param name="Method">The method that the COM object calls to raise the event.</param>
    /// <param name="HandlerName">The name of the method of the handler interface.</param>
    /// <param name="ReturnType">The return type, <c>void</c> when there is none.</param>
    /// <param name="Parameters">The parameters.</param>
    /// <param name="IsDispatched">Whether the event is raised through <c>IDispatch::Invoke</c>.</param>
    private sealed record Event(MethodDefinition Method, string HandlerName, string ReturnType, IReadOnlyList<SignatureParameter> Parameters, bool IsDispatched)
    {
        /// <summary>The name of the .NET event or of the observable of the sink class.</summary>
        public string Name { get; init; } = HandlerName;

        public bool IsVoid => ReturnType == "void";

        public bool HasPointers => ReturnType.EndsWith('*') || Parameters.Any(static p => p.TypeName.EndsWith('*'));
    }

    /// <summary>
    /// An event interface.
    /// </summary>
    /// <param name="Definition">The interface.</param>
    /// <param name="Guid">The interface identifier.</param>
    /// <param name="Events">The events.</param>
    /// <param name="VtableGaps">The vtable slots of the interface and its base interfaces that are not described by the type library.</param>
    /// <param name="IsDispatch">Whether the interface is a dispinterface.</param>
    /// <param name="IsDual">Whether the interface is a vtable interface derived from <c>IDispatch</c>.</param>
    private sealed record Source(InterfaceDefinition Definition, Guid Guid, IReadOnlyList<Event> Events, IReadOnlyList<int> VtableGaps, bool IsDispatch, bool IsDual)
    {
        public string InterfaceName => Syntax.Escape(Definition.Name);

        // Names that already look like interface names (IFooEvents, _IFooEvents) are not prefixed.
        public string HandlerName => Definition.Name.StartsWith('_') || Definition.Name is ['I', var second, ..] && char.IsUpper(second)
            ? Definition.Name + "Handler"
            : "I" + Definition.Name + "Handler";

        public string SinkName => Definition.Name + "Sink";

        public bool IsUnsafe => Events.Any(static e => e.HasPointers);

        public string DelegateName(Event @event) => $"{Definition.Name}_{@event.Name}EventHandler";

        public string EventArgsName(Event @event) => $"{Definition.Name}_{@event.Name}EventArgs";
    }
}
