using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("ScreenSplit")]
[assembly: AssemblyDescription("Dynamic Tiling Window Manager for Windows")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Crucialmer")]
[assembly: AssemblyProduct("ScreenSplit")]
[assembly: AssemblyCopyright("Copyright © 2026 Crucialmer")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("b4a86363-039a-499c-8723-871d8c24fbd9")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version
//      Build Number
//      Revision
//
[assembly: AssemblyVersion("2.19.1.0")]
[assembly: AssemblyFileVersion("2.19.1.0")]
[assembly: AssemblyInformationalVersion("2.19.1")]

// Expose internals to tests
[assembly: InternalsVisibleTo("FancyWM.Tests")]
// Wrap thrown non-exception type objects in RuntimeWrappedException in all handlers in this assembly.
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
