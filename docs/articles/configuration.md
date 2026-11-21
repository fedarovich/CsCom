# Configuration file

`--config` takes an XML file that adds transformers to the [pipeline](architecture.md). Each element of the `ModelTransformers` and
`SyntaxTransformers` sections adds the transformer with that name, configured by its attributes and child elements:

```xml
<CsCom>
  <ModelTransformers>
    <RenameType Type="^_(.*)$" NewName="$1" />
    <RenameMember Type="^IShape$" Member="^Get(.*)$" NewName="Fetch$1" />
    <SetMarshalling Type="^IArrays$" Member="^Matrix$" SafeArrays="wrapper" />
    <RemoveEnumPrefix />
  </ModelTransformers>
</CsCom>
```

- **Order:** the transformers run in the order of their elements, and each sees the model as the elements before it left it,
  e.g. the names that a rename produced and the types that `SetType` set. The model transformers run after the aliases are
  resolved and before the names are sanitized, so they see the resolved aliases and the names they produce are made valid
  and unique.
- **Validation:** unknown transformers, attributes and child elements are reported with their location, e.g.
  `cscom.xml(3,6): Unknown model transformer <Rename>.` Nothing is written when the file is not valid.
- **`project update`:** `project create` records the files in `cscom.json` by library name, relative to the project directory, and
  `project update` applies them again. `--config LIBRARY=PATH` replaces the file of a library, `--config LIBRARY=` removes it, and
  `--config ""` removes all of them.
- **Custom transformers:** they are registered through the API with `InteropPipeline.Registry.AddModelTransformer(name, element => ...)`
  and `AddSyntaxTransformer`, whose factories read their element with `ConfigurationElement`.
- **Schema and starter file:** `cscom config create [-o <dir>]` writes `cscom.xsd`, the schema of the configuration files, and a
  `cscom.xml` with commented examples of every transformer, unless it already exists (the schema is always written again). The
  starter file refers to the schema with `xsi:noNamespaceSchemaLocation`, which gives IntelliSense in Visual Studio, VS Code and
  other XML editors.

## Selectors

The selectors match the current names: the type library names, unless an earlier transformer renamed them.
[`cscom list`](commands.md#listing-a-type-library) shows these names.
- **Regular expressions:** `Type`, `Member` and `Parameter` are .NET regular expressions. Like `Regex.IsMatch`, an expression
  matches anywhere in the name, so `^IShape$` is needed to match only `IShape`. The matching is case-sensitive; `(?i)` makes it
  case-insensitive.
- **Exact names:** `TypeName`, `MemberName` and `ParameterName` match a whole name exactly. They cannot be combined with the
  regular expression of the same selector.
- **Kinds:** `Kind` restricts the types to a list of kinds separated by `|` or `,`: `enum`, `struct`, `union`, `interface`,
  `dispinterface`, `coclass` and `module` (the names of `cscom list`), e.g. `Kind="interface|dispinterface"`.
- **Omitted selectors:** an omitted type, member or parameter selector matches everything, where the transformer allows it.
- **New names:** with a regular expression, `NewName` replaces the matches of the last selector, like `Regex.Replace`, so it can use
  substitutions such as `$1`: `Type="Shape" NewName="Figure"` renames `IShapeEvents` to `IFigureEvents`. With an exact name,
  `NewName` is the new name as is. Invalid characters become `_`, and names that clash get a `_` suffix.
- **No match:** a transformer whose selectors match nothing logs a warning, e.g. `RenameType: no type matches Type='^IShap$'`, so
  the same file keeps working with other versions of the type library.

## Model transformers

The type selector (`Type`/`TypeName`) can be combined with `Kind`, except for the transformers that only apply to enums.

| Element | Attributes | Effect |
| --- | --- | --- |
| `RenameType` | `Type`, `NewName` | Renames the matching types. |
| `RenameMember` | `Type` (optional), `Member`, `Parameter` (optional), `NewName` | Renames the matching members: methods and properties of interfaces (the accessors of a property together), fields of structures, members of enums, and functions and constants of modules. With `Parameter`, renames the matching parameters of the matching methods instead. |
| `RenameMembersToPascalCase` | `Type` and `Member` (both optional) | Converts the names of the matching members, or of all members, to PascalCase, see below. Parameters and types are not renamed. |
| `SetMarshalling` | `Type`, `Member` and `Parameter` (all optional); at least one of `Dates`, `Currencies`, `Booleans` (`bcl\|none\|wrapper`), `Bstrs` (`string\|wrapper\|none`), `SafeArrays` (`array\|sysarray\|wrapper\|none`) and `Interfaces` (`shared\|unique`) | Changes the [marshalling options](marshalling.md) of the parameters and return values of the matching methods, and of the matching dispinterface properties and structure fields. With `Parameter`, only of the matching parameters, including `[retval]` parameters. Later elements override the values set by earlier ones. A type that none of the attributes applies to does not match: only `SafeArrays` applies to dispinterfaces, and only `Dates`, `Currencies`, `Booleans` and `Bstrs` to structures; `Interfaces` only applies to interface pointers that are not passed in and out, see [Interface pointers](interface-pointers.md): the other attributes apply to the matching parameters whatever their types, but an element with only `Interfaces` applies to the interface pointers at its point, so a `SetType` that makes a parameter an interface pointer goes before it. |
| `RemoveEnumPrefix` | `Type` (optional) | Removes the prefix shared by the members of the matching enums, or of all enums. |
| `ExcludeType` | `Type` and/or `Kind` | Removes the matching types. References to interfaces, dispinterfaces and coclasses become `IDispatch*` (if they are dispatchable) or `IUnknown*`, references to enums `long`, and pointers to structures `void*`. Structures used by value and base interfaces of kept interfaces cannot be removed. |
| `SetType` | `Type` (optional), `Member`, `Parameter` (optional), `To` | Changes the type of the matching parameters, or without `Parameter` of the members themselves (return values, property values, dispinterface properties, structure fields), to the type named `To` of the library, or of another library that is generated with it, e.g. a `long` that is meant to be an enum. The type is only required when something matches. Parameters passed by reference keep their pointer. Vtable and dual interfaces, functions and structures only accept compatible types: 4-byte integers to enums, interface pointers to interfaces, and structures of the same size; dispinterfaces accept any type. |
| `SetSafeArrayRank` | `Type`, `Member` and `Parameter` (all optional), `Rank` (`1`–`3`) | Sets the number of dimensions of the `SAFEARRAY`s of the matching parameters, return values and dispinterface properties, see [SAFEARRAYs](safearrays.md). |

```xml
<RenameMember Type="^IShape$" Member="^Move$" Parameter="^d(.)$" NewName="delta$1" />    <!-- dx, dy -> deltax, deltay -->
<SetMarshalling Type="^IStrings$" Member="^Echo$" Parameter="^Value$" Bstrs="none" />   <!-- Echo(nint Value) -->
<SetMarshalling Type="^Record$" Member="^Date$" Dates="wrapper" />                       <!-- a ComDate field -->
<RenameMembersToPascalCase Kind="enum|struct" />                                         <!-- MAX_VALUE -> MaxValue, checked -> Checked -->
```

`RemoveEnumPrefix` removes the longest common prefix that ends at a word boundary, after an underscore or before an upper case letter
that follows a lower case letter or a digit: `xlArea`/`xlBar` become `Area`/`Bar` and `SHAPE_CIRCLE`/`SHAPE_SQUARE` become
`CIRCLE`/`SQUARE`. Enums with a single member, or whose members would not start with a letter (`Size16`/`Size32`), are not changed.

`RenameMembersToPascalCase` splits a name into words at the characters that are not letters or digits, usually `_`, and joins them
with the first letter of each word in upper case. Words in upper case with more than two letters become title case, and the other
words keep the rest of their letters, so names that are PascalCase already, acronyms included, are kept:

| Name | New name |
| --- | --- |
| `MAX_VALUE`, `checked`, `_NewEnum`, `xlArea` | `MaxValue`, `Checked`, `NewEnum`, `XlArea` |
| `HWND`, `IO_ERROR`, `OK_BUTTON` | `Hwnd`, `IOError`, `OKButton` (two-letter words stay in upper case) |
| `Count`, `XMLDocument`, `IO`, `Value2` | not changed |
| property `SOME_PROPERTY` | `SomeProperty`, with the accessors `get_SomeProperty` and `set_SomeProperty` |
| method `get_SomeValue` | `GetSomeValue` |

- **Accessors:** the accessors of a property share its name, so only the part after `get_`, `set_` or `putref_` changes. Ordinary
  methods whose names look like accessors are converted like any other name.
- **Collisions:** a member keeps its name, with a warning, if the new name is the name of another member of the type, e.g.
  `count` next to `Count`. Names that would be empty or start with a digit, such as `_1`, are kept as well.
- **Enum prefixes:** with `RemoveEnumPrefix`, either order gives the same names for enums like `SHAPE_CIRCLE`/`SHAPE_SQUARE`:
  `Circle`/`Square`.

## Syntax transformers

The syntax transformers run after the code is generated, so their selectors match the final names, after the model transformers.

| Element | Attributes | Effect |
| --- | --- | --- |
| `AddFlagsAttribute` | `Type` | Adds `[System.Flags]` to the matching enums, which type libraries cannot mark as bit fields. Enums that already have it are not changed. |
| `AddAttribute` | `Type` and/or `Kind`, `Member` (optional), `Role` (optional), `Attribute` | Adds a C# attribute (without brackets) to the declarations generated for the matching types or members. `Role` selects which declaration: `Declaration` (default), `Extensions` (the extensions class of an interface), `EventHandler`, `EventSink`, `EventDelegate`, `EventArgs`, `Event`, `Property`, `BackingField`, `InlineArray`, `Enumerator`, `Overload` (the [overloads](optional-parameters.md) of a method) or `NamedIndexer` (the proxy of a [named indexer](named-indexers.md) and its indexer). Declarations that already have the attribute are not changed. |

```xml
<SyntaxTransformers>
  <AddFlagsAttribute Type="^(ShapeFlags|Options)$" />
  <AddAttribute TypeName="ICircle" Attribute='System.Obsolete("Use IShape")' />
  <AddAttribute TypeName="IShape" Role="Extensions" Attribute="System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)" />
</SyntaxTransformers>
```
