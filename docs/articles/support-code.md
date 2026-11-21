# Support code

The generated code depends on a few support types: `IDispatch`, `IEnumVARIANT`, `ComActivator`, `ComMarshalling`, the `VARIANT`, `SAFEARRAY`, `BSTR`, `DATE` and `CURRENCY` marshallers, the
base class of the late-bound wrappers and the event sinks; with `--dynamic`, also the `dynamic` support. With `--support-code package`, the generated project references the **CsCom.Runtime**
NuGet package, and **CsCom.Runtime.Dynamic** with `--dynamic`. With `public` or `internal`, the support code is emitted into the project, in the `<RootNamespace>.ComSupport`
namespace: under the namespace of a single source, or the root namespace of several sources, see [Several sources](sources.md#several-sources). With
CsCom.Build, it is emitted into `CsComSupportNamespace`, `$(RootNamespace).ComSupport` by default.
In the `internal` mode all generated types are internal, because a public interface cannot derive from an internal `IDispatch`.

The generated projects disable runtime marshalling, which source-generated COM interop requires to marshal `VARIANT`s and `DECIMAL`s.
