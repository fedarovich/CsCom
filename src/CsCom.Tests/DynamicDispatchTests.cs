using System.Runtime.InteropServices;
using CsCom.Runtime;
using Microsoft.CSharp.RuntimeBinder;

namespace CsCom.Tests;

[TestClass]
public sealed class DynamicDispatchTests
{
    private static readonly Guid DictionaryClsid = new("EE09B103-97E0-11CF-978F-00A02463E06F");
    private static readonly string TempFolder = Path.GetTempPath().TrimEnd('\\');

    [TestMethod]
    public void InvokesMethodsAndProperties()
    {
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");

        dictionary.Add("a", 1);
        dictionary.Add("b", "two");

        Assert.AreEqual(2, (int)dictionary.Count);
        Assert.IsTrue((bool)dictionary.Exists("a"));
        Assert.AreEqual(1, (int)dictionary.Item("a"));
        Assert.AreEqual("two", (string)dictionary.Item("b"));
    }

    [TestMethod]
    public void UsesTheDefaultMemberForIndexers()
    {
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");

        dictionary["a"] = 1;
        dictionary["a"] = 2;

        Assert.AreEqual(2, (int)dictionary["a"]);
        Assert.AreEqual(1, (int)dictionary.Count);
    }

    [TestMethod]
    public void SetsProperties()
    {
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");

        dictionary.CompareMode = 1;
        dictionary.Add("Key", 1);

        Assert.AreEqual(1, (int)dictionary.CompareMode);
        Assert.IsTrue((bool)dictionary.Exists("KEY"));
    }

    [TestMethod]
    public void EnumeratesCollections()
    {
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");
        dictionary.Add("a", 1);
        dictionary.Add("b", 2);

        var keys = new List<string>();
        foreach (var key in dictionary)
        {
            keys.Add((string)key);
        }

        CollectionAssert.AreEqual(new[] { "a", "b" }, keys);
    }

    [TestMethod]
    public void PassesNamedArguments()
    {
        dynamic fileSystem = ComActivator.CreateDynamicInstance("Scripting.FileSystemObject");

        Assert.AreEqual(@"C:\a\b", (string)fileSystem.BuildPath(Path: @"C:\a", Name: "b"));
        Assert.AreEqual(@"C:\a\b", (string)fileSystem.BuildPath(Name: "b", Path: @"C:\a"));
        Assert.AreEqual(@"C:\a\b", (string)fileSystem.BuildPath(@"C:\a", Name: "b"));
        Assert.ThrowsExactly<ArgumentException>(() => fileSystem.BuildPath(Path: @"C:\a", Missing: "b"));
    }

    [TestMethod]
    public void WrapsReturnedObjects()
    {
        dynamic fileSystem = ComActivator.CreateDynamicInstance("Scripting.FileSystemObject");

        dynamic folder = fileSystem.GetFolder(TempFolder);

        Assert.IsInstanceOfType<DynamicDispatchObject>(folder);
        Assert.AreEqual(Path.GetFileName(TempFolder), (string)folder.Name, ignoreCase: true);
        Assert.IsGreaterThanOrEqualTo(0, (int)fileSystem.GetFolder(TempFolder).Files.Count);
        Assert.IsTrue((bool)folder.Drive.IsReady);
    }

    [TestMethod]
    public void PassesDynamicObjectsAsArguments()
    {
        dynamic fileSystem = ComActivator.CreateDynamicInstance("Scripting.FileSystemObject");
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");

        dictionary.Add("folder", fileSystem.GetFolder(TempFolder));
        dictionary["drive"] = fileSystem.GetFolder(TempFolder).Drive;

        Assert.AreEqual(Path.GetFileName(TempFolder), (string)dictionary.Item("folder").Name, ignoreCase: true);
        Assert.AreEqual(Path.GetPathRoot(TempFolder)![0], ((string)dictionary["drive"].DriveLetter)[0]);
    }

    [TestMethod]
    public void ConvertsToInterfacesAndDefaultMemberValues()
    {
        dynamic fileSystem = ComActivator.CreateDynamicInstance("Scripting.FileSystemObject");

        IDispatch dispatch = fileSystem;
        string path = fileSystem.GetFolder(TempFolder);

        Assert.AreEqual(0, dispatch.GetTypeInfoCount(out uint count));
        Assert.AreEqual(1u, count);
        Assert.AreEqual(TempFolder, path, ignoreCase: true);
    }

    [TestMethod]
    public void ReportsUnknownMembersAndComErrors()
    {
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");
        dictionary.Add("a", 1);

        Assert.ThrowsExactly<RuntimeBinderException>(() => dictionary.NoSuchProperty);
        Assert.ThrowsExactly<RuntimeBinderException>(() => dictionary.NoSuchMethod(1));
        var exception = Assert.ThrowsExactly<COMException>(() => dictionary.Add("a", 2));
        Assert.AreEqual(unchecked((int)0x800A01C9), exception.HResult);
    }

    [TestMethod]
    public void WrapsExistingObjects()
    {
        var dispatch = ComActivator.CreateInstance<IDispatch>(DictionaryClsid);
        dynamic fromInterface = dispatch.AsDynamic();
        fromInterface.Add("a", 1);

        dynamic fromObject = ComDynamic.ToDynamic(ComActivator.CreateInstance<object>("Scripting.Dictionary"))!;
        fromObject.Add("b", 2);

        Assert.AreEqual(1, (int)fromInterface.Count);
        Assert.AreEqual(1, (int)fromObject.Count);
        Assert.IsNull(ComDynamic.ToDynamic(null));
        Assert.ThrowsExactly<InvalidCastException>(() => ComDynamic.ToDynamic("not a COM object"));
    }

    [TestMethod]
    public void ResolvesProgIds()
    {
        Assert.AreEqual(DictionaryClsid, ComActivator.GetClassId("Scripting.Dictionary"));
        var exception = Assert.ThrowsExactly<COMException>(() => ComActivator.GetClassId("CsCom.No.Such.Class"));
        StringAssert.Contains(exception.Message, "CsCom.No.Such.Class");
    }
}
