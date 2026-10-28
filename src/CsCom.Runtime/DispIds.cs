namespace CsCom.Runtime;

/// <summary>
/// Well-known dispatch identifiers.
/// </summary>
public static class DispIds
{
    /// <summary>The default member (<c>DISPID_VALUE</c>).</summary>
    public const int Value = 0;

    /// <summary>An unknown member (<c>DISPID_UNKNOWN</c>).</summary>
    public const int Unknown = -1;

    /// <summary>The parameter that receives the value of an assignment (<c>DISPID_PROPERTYPUT</c>).</summary>
    public const int PropertyPut = -3;

    /// <summary>The enumerator method of a collection (<c>DISPID_NEWENUM</c>).</summary>
    public const int NewEnum = -4;

    /// <summary>The <c>Evaluate</c> method (<c>DISPID_EVALUATE</c>).</summary>
    public const int Evaluate = -5;
}

/// <summary>
/// The kind of an <see cref="IDispatch.Invoke"/> call.
/// </summary>
[System.Flags]
public enum DispatchFlags : ushort
{
    /// <summary>The member is invoked as a method (<c>DISPATCH_METHOD</c>).</summary>
    Method = 0x1,

    /// <summary>The member is retrieved as a property (<c>DISPATCH_PROPERTYGET</c>).</summary>
    PropertyGet = 0x2,

    /// <summary>The member is changed as a property (<c>DISPATCH_PROPERTYPUT</c>).</summary>
    PropertyPut = 0x4,

    /// <summary>The member is changed by a reference assignment (<c>DISPATCH_PROPERTYPUTREF</c>).</summary>
    PropertyPutRef = 0x8,
}
