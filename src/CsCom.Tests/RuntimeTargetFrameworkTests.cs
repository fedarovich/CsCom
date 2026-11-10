using System.Reflection;
using System.Runtime.Versioning;
using CsCom.Runtime;

namespace CsCom.Tests;

[TestClass]
public sealed class RuntimeTargetFrameworkTests
{
    // CsCom.Runtime and CsCom.Runtime.Dynamic have a build for each .NET version, which the tests of that version use.
    [TestMethod]
    [DataRow(typeof(ComActivator))]
    [DataRow(typeof(ComDynamic))]
    public void RuntimeAssembliesTargetTheFrameworkOfTheTests(Type type) =>
        Assert.AreEqual(TestEnvironment.FrameworkName, type.Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
}
