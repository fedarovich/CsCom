namespace CsCom.Model;

/// <summary>
/// Replaces type references everywhere in a <see cref="TypeLibrary"/>.
/// </summary>
public static class TypeReferenceRewriter
{
    /// <summary>
    /// Replaces every type reference in the library with the result of <paramref name="rewrite"/>.
    /// </summary>
    /// <param name="library">The library.</param>
    /// <param name="rewrite">Rewrites a type reference.</param>
    /// <param name="deep">
    /// Whether the function is applied to the element types first, then to the containing pointer or array types; otherwise
    /// it is applied once to each whole type reference, and rewrites the element types itself if needed.
    /// </param>
    public static void Rewrite(TypeLibrary library, Func<TypeReference, TypeReference> rewrite, bool deep = true)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(rewrite);

        TypeReference Apply(TypeReference type) => deep ? RewriteDeep(type, rewrite) : rewrite(type);

        foreach (var type in library.Types)
        {
            switch (type)
            {
                case RecordDefinition record:
                    foreach (var field in record.Fields)
                    {
                        field.Type = Apply(field.Type);
                    }

                    break;
                case AliasDefinition alias:
                    alias.Target = Apply(alias.Target);
                    break;
                case InterfaceDefinition @interface:
                    if (@interface.BaseInterface is not null)
                    {
                        @interface.BaseInterface = Apply(@interface.BaseInterface);
                    }

                    RewriteMethods(@interface.Methods, Apply);
                    foreach (var property in @interface.Properties)
                    {
                        property.Type = Apply(property.Type);
                    }

                    break;
                case CoClassDefinition coClass:
                    for (int i = 0; i < coClass.Interfaces.Count; i++)
                    {
                        coClass.Interfaces[i] = coClass.Interfaces[i] with { Interface = Apply(coClass.Interfaces[i].Interface) };
                    }

                    break;
                case ModuleDefinition module:
                    RewriteMethods(module.Functions, Apply);
                    foreach (var constant in module.Constants)
                    {
                        constant.Type = Apply(constant.Type);
                    }

                    break;
            }
        }
    }

    private static void RewriteMethods(List<MethodDefinition> methods, Func<TypeReference, TypeReference> apply)
    {
        foreach (var method in methods)
        {
            method.ReturnType = apply(method.ReturnType);
            foreach (var parameter in method.Parameters)
            {
                parameter.Type = apply(parameter.Type);
            }
        }
    }

    private static TypeReference RewriteDeep(TypeReference type, Func<TypeReference, TypeReference> rewrite)
    {
        TypeReference inner = type switch
        {
            PointerTypeReference pointer => RewriteElement(pointer, pointer.ElementType, static (p, e) => p with { ElementType = e }, rewrite),
            SafeArrayTypeReference array => RewriteElement(array, array.ElementType, static (a, e) => a with { ElementType = e }, rewrite),
            FixedArrayTypeReference array => RewriteElement(array, array.ElementType, static (a, e) => a with { ElementType = e }, rewrite),
            _ => type,
        };

        return rewrite(inner);
    }

    private static TypeReference RewriteElement<T>(T container, TypeReference element, Func<T, TypeReference, T> update, Func<TypeReference, TypeReference> rewrite)
        where T : TypeReference
    {
        TypeReference newElement = RewriteDeep(element, rewrite);
        return ReferenceEquals(newElement, element) ? container : update(container, newElement);
    }
}
