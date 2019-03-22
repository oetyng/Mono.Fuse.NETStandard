//
// create-native-map.cs: Builds a C map of constants defined on C# land
//
// Authors:
//  Miguel de Icaza (miguel@novell.com)
//  Jonathan Pryor (jonpryor@vt.edu)
//
// (C) 2003 Novell, Inc.
// (C) 2004-2005 Jonathan Pryor
//

//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Fuse.NETStandard;

delegate void CreateFileHandler (string assembly_name, string file_prefix);
delegate void AssemblyAttributesHandler (Assembly assembly);
delegate void TypeHandler (Type t, string ns, string fn);
delegate void CloseFileHandler (string file_prefix);

namespace CreateNativeMap
{
	internal class MakeMap
	{
		public static int Main(string[] args)
		{
			FileGenerator[] generators = new FileGenerator[]
			{
				new HeaderFileGenerator(),
				new SourceFileGenerator(),
				new ConvertFileGenerator(),
				new ConvertDocFileGenerator(),
			};

			var config = new Configuration();
			var exit = false;
			try
			{
				exit = !config.Parse(args);
			}
			catch (Exception e)
			{
				Console.WriteLine("{0}: error: {1}",
					Environment.GetCommandLineArgs()[0], e.Message);
				exit = true;
			}

			if (exit)
			{
				Configuration.ShowHelp();
				return 1;
			}

			MapUtils.config = config;

			var composite = new MakeMap();
			foreach (FileGenerator g in generators)
			{
				g.Configuration = config;
				composite.FileCreators += new CreateFileHandler(g.CreateFile);
				composite.AssemblyAttributesHandler +=
					new AssemblyAttributesHandler(g.WriteAssemblyAttributes);
				composite.TypeHandler += new TypeHandler(g.WriteType);
				composite.FileClosers += new CloseFileHandler(g.CloseFile);
			}

			return composite.Run(config);
		}

		event CreateFileHandler FileCreators;
		event AssemblyAttributesHandler AssemblyAttributesHandler;
		event TypeHandler TypeHandler;
		event CloseFileHandler FileClosers;

		int Run(Configuration config)
		{
			FileCreators(config.AssemblyFileName, config.OutputPrefix);

			var assembly = Assembly.LoadFrom(config.AssemblyFileName);
			AssemblyAttributesHandler(assembly);

			Type[] exported_types = assembly.GetTypes();
			Array.Sort(exported_types, new TypeFullNameComparer());

			foreach (Type t in exported_types)
			{
				string ns = MapUtils.GetNamespace(t);
				/*
				if (ns == null || !ns.StartsWith ("Mono"))
					continue;
				 */
				string fn = MapUtils.GetManagedType(t);

				TypeHandler(t, ns, fn);
			}

			FileClosers(config.OutputPrefix);

			return 0;
		}

		class TypeFullNameComparer : IComparer<Type>
		{
			public int Compare(Type t1, Type t2)
			{
				if (t1 == t2)
					return 0;
				if (t1 == null)
					return 1;
				if (t2 == null)
					return -1;
				return CultureInfo.InvariantCulture.CompareInfo.Compare(
					t1.FullName, t2.FullName, CompareOptions.Ordinal);
			}
		}
	}

	class Configuration
	{
		readonly Dictionary<string, string> _renameMembers = new Dictionary<string, string>();
        readonly Dictionary<string, string> _renameNamespaces = new Dictionary<string, string>();

        delegate void ArgumentHandler(Configuration c, string name, string value);

		readonly static Dictionary<string, ArgumentHandler> _handlers;

		static Configuration()
		{
            _handlers = new Dictionary<string, ArgumentHandler>
            {
                ["autoconf-header"] = delegate (Configuration c, string name, string value) { c.ImplementationHeaders.Add("ah:" + name); },
                ["autoconf-member"] = delegate (Configuration c, string name, string value) { c.AutoconfMembers.Add(name); },
                ["impl-header"] = delegate (Configuration c, string name, string value) { c.ImplementationHeaders.Add(name); },
                ["impl-macro"] = delegate (Configuration c, string name, string value)
                {
                    if (value != null)
                        name += "=" + value;
                    c.ImplementationMacros.Add(name);
                },
                ["library"] = delegate (Configuration c, string name, string value) { c.NativeLibraries.Add(name); },
                ["exclude-native-symbol"] = delegate (Configuration c, string name, string value) { c.NativeExcludeSymbols.Add(name); },
                ["public-header"] = delegate (Configuration c, string name, string value) { c.PublicHeaders.Add(name); },
                ["public-macro"] = delegate (Configuration c, string name, string value)
                {
                    if (value != null)
                        name += "=" + value;
                    c.PublicMacros.Add(name);
                },
                ["rename-member"] = delegate (Configuration c, string name, string value)
                {
                    c._renameMembers[name] = value ?? throw new Exception("missing rename value");
                },
                ["rename-namespace"] = delegate (Configuration c, string name, string value)
                {
                    if (value == null)
                        throw new Exception("missing rename value");

                    value = value.Replace(".", "_");
                    c._renameNamespaces[name] = value;
                }
            };
        }

		public Configuration()
		{ }

        public List<string> NativeLibraries { get; } = new List<string>();
        public List<string> AutoconfMembers { get; } = new List<string>();
        public List<string> NativeExcludeSymbols { get; } = new List<string>();
        public List<string> PublicHeaders { get; } = new List<string>();
        public List<string> PublicMacros { get; } = new List<string>();
        public List<string> ImplementationHeaders { get; } = new List<string>();
        public List<string> ImplementationMacros { get; } = new List<string>();

        public IDictionary<string, string> MemberRenames => _renameMembers;
        public IDictionary<string, string> NamespaceRenames => _renameNamespaces;

        public string AssemblyFileName { get; private set; }
        public string OutputPrefix { get; private set; }

        const string NameValue = @"(?<Name>[^=]+)(=(?<Value>.*))?";
		const string Argument = @"^--(?<Argument>[\w-]+)([=:]" + NameValue + ")?$";

		public bool Parse(string[] args)
		{
			var argRE = new Regex(Argument);
			var valRE = new Regex(NameValue);

			for (int i = 0; i < args.Length; ++i)
			{
				Match m = argRE.Match(args[i]);
				if (m.Success)
				{
					string arg = m.Groups["Argument"].Value;
					if (arg == "help")
						return false;
					if (!m.Groups["Name"].Success)
					{
						if ((i + 1) >= args.Length)
							throw new Exception($"missing value for argument {args[i]}");
						m = valRE.Match(args[++i]);
						if (!m.Success)
							throw new Exception($"invalid value for argument {args[i - 1]}: {args[i]}");
					}

					string name = m.Groups["Name"].Value;
					string value = m.Groups["Value"].Success ? m.Groups["Value"].Value : null;
					if (_handlers.ContainsKey(arg))
						_handlers[arg](this, name, value);
					else
						throw new Exception("invalid argument " + arg);
				}
				else if (AssemblyFileName == null)
					AssemblyFileName = args[i];
				else
					OutputPrefix = args[i];
			}

			if (AssemblyFileName == null)
				throw new Exception("missing ASSEMBLY");
			if (OutputPrefix == null)
				throw new Exception("missing OUTPUT-PREFIX");

			NativeLibraries.Sort();
			AutoconfMembers.Sort();
			NativeExcludeSymbols.Sort();

			return true;
		}

		public static void ShowHelp()
		{
			Console.WriteLine(
				"Usage: create-native-map \n" +
				"\t[--autoconf-header=HEADER]* \n" +
				"\t[--autoconf-member=MEMBER]* \n" +
				"\t[--exclude-native-symbol=SYMBOL]*\n" +
				"\t[--impl-header=HEADER]* \n" +
				"\t[--impl-macro=MACRO]* \n" +
				"\t[--library=LIBRARY]+ \n" +
				"\t[--public-header=HEADER]* \n" +
				"\t[--public-macro=MACRO]* \n" +
				"\t[--rename-member=FROM=TO]* \n" +
				"\t[--rename-namespace=FROM=TO]*\n" +
				"\tASSEMBLY OUTPUT-PREFIX"
			);
		}
	}

	static class MapUtils
	{
		internal static Configuration config;

		public static T GetCustomAttribute<T>(MemberInfo element) where T : Attribute
		    => (T)Attribute.GetCustomAttribute(element, typeof(T), true);

		public static T GetCustomAttribute<T>(Assembly assembly) where T : Attribute
		    => (T)Attribute.GetCustomAttribute(assembly, typeof(T), true);

		public static T[] GetCustomAttributes<T>(MemberInfo element) where T : Attribute
		    => (T[])Attribute.GetCustomAttributes(element, typeof(T), true);

		public static T[] GetCustomAttributes<T>(Assembly assembly) where T : Attribute
		    => (T[])Attribute.GetCustomAttributes(assembly, typeof(T), true);

		public static MapAttribute GetMapAttribute(ICustomAttributeProvider element)
		{
			foreach (object o in element.GetCustomAttributes(true))
			{
				if (!IsMapAttribute(o))
					continue;
				string nativeType = GetPropertyValueAsString(o, "NativeType");
				MapAttribute map = nativeType == null
					? new MapAttribute()
					: new MapAttribute(nativeType);
				map.SuppressFlags = GetPropertyValueAsString(o, "SuppressFlags");
				return map;
			}

			return null;
		}

		static bool IsMapAttribute(object o)
		{
			Type t = o.GetType();
			do
			{
				if (t.Name == "MapAttribute")
					return true;

				t = t.BaseType;
			} while (t != null);

			return false;
		}

		static string GetPropertyValueAsString(object o, string property)
		{
			object v = GetPropertyValue(o, property);
			string s = v?.ToString();
			if (s != null)
				return s.Length == 0 ? null : s;
			return null;
		}

		static object GetPropertyValue(object o, string property)
		{
			PropertyInfo p = o.GetType().GetProperty(property);
			if (p == null)
				return null;
			if (!p.CanRead)
				return null;
			return p.GetValue(o, new object[0]);
		}

		public static bool IsIntegralType(Type t)
		{
			return t == typeof(byte) || t == typeof(sbyte) || t == typeof(char) ||
			       t == typeof(short) || t == typeof(ushort) ||
			       t == typeof(int) || t == typeof(uint) ||
			       t == typeof(long) || t == typeof(ulong);
		}

		public static bool IsBlittableType(Type t)
		    => IsIntegralType(t) || t == typeof(IntPtr) || t == typeof(UIntPtr);

		public static string GetNativeType(Type t)
		{
			Type et = GetElementType(t);
			string ut = et.Name;
			if (et.IsEnum)
				ut = Enum.GetUnderlyingType(et).Name;

			string type = null;

			switch (ut)
			{
				case "Boolean":
					type = "int";
					break;
				case "Byte":
					type = "unsigned char";
					break;
				case "SByte":
					type = "signed char";
					break;
				case "Int16":
					type = "short";
					break;
				case "UInt16":
					type = "unsigned short";
					break;
				case "Int32":
					type = "int";
					break;
				case "UInt32":
					type = "unsigned int";
					break;
				case "Int64":
					type = "gint64";
					break;
				case "UInt64":
					type = "guint64";
					break;
				case "IntPtr":
					type = "void*";
					break;
				case "UIntPtr":
					type = "void*";
					break;
				case "String":
					type = "const char";
					break; /* ref type */
				case "StringBuilder":
					type = "char";
					break; /* ref type */
				case "Void":
					type = "void";
					break;
				case "HandleRef":
					type = "void*";
					break;
			}

			bool isDelegate = IsDelegate(t);
			if (type == null)
				type = isDelegate ? t.Name : GetStructName(t);
			if (!et.IsValueType && !isDelegate)
				type += "*";

			while (t.HasElementType)
			{
				t = t.GetElementType();
				type += "*";
			}

			return type;
			//return (t.IsByRef || t.IsArray || (!t.IsValueType && !isDelegate)) ? type + "*" : type;
		}

		public static bool IsDelegate(Type t)
		    => typeof(Delegate).IsAssignableFrom(t);

		static string GetStructName(Type t)
		{
			t = GetElementType(t);
			return "struct " + GetManagedType(t);
		}

		public static Type GetElementType(Type t)
		{
			while (t.HasElementType)
				t = t.GetElementType();

			return t;
		}

		public static string GetNamespace(Type t)
		{
			if (t.Namespace == null)
				return "";
			if (config.NamespaceRenames.ContainsKey(t.Namespace))
				return config.NamespaceRenames[t.Namespace];
			return t.Namespace.Replace('.', '_');
		}

		public static string GetManagedType(Type t)
		{
			string ns = GetNamespace(t);
			string tn =
				(t.DeclaringType != null ? t.DeclaringType.Name + "_" : "") + t.Name;
			return ns + "_" + tn;
		}

		public static string GetNativeType(FieldInfo field)
		{
			MapAttribute map =
				GetMapAttribute(field)
				??
				GetMapAttribute(field.FieldType);
			if (map != null)
				return map.NativeType;
			return null;
		}

		public static string GetFunctionDeclaration(string name, MethodInfo method)
		{
			var sb = new StringBuilder();
#if false
		Console.WriteLine (t);
		foreach (object o in t.GetMembers ())
			Console.WriteLine ("\t" + o);
#endif
			sb.Append(method.ReturnType == typeof(string)
				? "char*"
				: MapUtils.GetNativeType(method.ReturnType));
			sb.Append(" ").Append(name).Append(" (");


			ParameterInfo[] parameters = method.GetParameters();
			if (parameters.Length == 0)
				sb.Append("void");
			else
			{
				if (parameters.Length > 0)
					WriteParameterDeclaration(sb, parameters[0]);

				for (int i = 1; i < parameters.Length; ++i)
				{
					sb.Append(", ");
					WriteParameterDeclaration(sb, parameters[i]);
				}
			}

			sb.Append(")");
			return sb.ToString();
		}

		static void WriteParameterDeclaration(StringBuilder sb, ParameterInfo pi)
		{
			// DumpTypeInfo (pi.ParameterType);
			string nt = GetNativeType(pi.ParameterType);
			sb.AppendFormat("{0} {1}", nt, pi.Name);
		}

		internal class _MemberNameComparer : IComparer<MemberInfo>, IComparer<FieldInfo>
		{
			public int Compare(FieldInfo m1, FieldInfo m2)
			    => Compare((MemberInfo)m1, (MemberInfo)m2);

			public int Compare(MemberInfo m1, MemberInfo m2)
			{
				if (m1 == m2)
					return 0;
				if (m1 == null)
					return 1;
				if (m2 == null)
					return -1;
				return CultureInfo.InvariantCulture.CompareInfo.Compare(
					m1.Name, m2.Name, CompareOptions.Ordinal);
			}
		}

		class _OrdinalStringComparer : IComparer<string>
		{
			public int Compare(string s1, string s2)
			{
				if (object.ReferenceEquals(s1, s2))
					return 0;
				if (s1 == null)
					return 1;
				if (s2 == null)
					return -1;
				return CultureInfo.InvariantCulture.CompareInfo.Compare(s1, s2,
					CompareOptions.OrdinalIgnoreCase);
			}
		}

		internal static _MemberNameComparer MemberNameComparer = new _MemberNameComparer();
		internal static IComparer<string> OrdinalStringComparer = new _OrdinalStringComparer();
	}

	abstract class FileGenerator
	{
        public Configuration Configuration { get; set; }

        public abstract void CreateFile(string assembly_name, string file_prefix);

		public virtual void WriteAssemblyAttributes(Assembly assembly)
		{ }

		public abstract void WriteType(Type t, string ns, string fn);
		public abstract void CloseFile(string file_prefix);

		protected static void WriteHeader(StreamWriter s, string assembly)
			=> WriteHeader(s, assembly, false);

		protected static void WriteHeader(StreamWriter s, string assembly, bool noConfig)
		{
			s.WriteLine(
				"/*\n" +
				" * This file was automatically generated by create-native-map from {0}.\n" +
				" *\n" +
				" * DO NOT MODIFY.\n" +
				" */",
				assembly);
			if (!noConfig)
			{
				s.WriteLine("#ifdef HAVE_CONFIG_H");
				s.WriteLine("#include <config.h>");
				s.WriteLine("#endif /* ndef HAVE_CONFIG_H */");
			}

			s.WriteLine();
		}

		protected static bool CanMapType(Type t)
		    => MapUtils.GetMapAttribute(t) != null;

		protected static bool IsFlagsEnum(Type t)
		    => t.IsEnum &&
			       MapUtils.GetCustomAttributes<FlagsAttribute>(t).Length > 0;

		protected static void SortFieldsInOffsetOrder(Type t, FieldInfo[] fields)
		{
			Array.Sort(fields, delegate(FieldInfo f1, FieldInfo f2)
			{
				long o1 = (long) Marshal.OffsetOf(f1.DeclaringType, f1.Name);
				long o2 = (long) Marshal.OffsetOf(f2.DeclaringType, f2.Name);
				return o1.CompareTo(o2);
			});
		}

		protected static void WriteMacroDefinition(TextWriter writer, string macro)
		{
			if (macro == null || macro.Length == 0)
				return;
			string[] val = macro.Split('=');
			writer.WriteLine("#ifndef {0}", val[0]);
			writer.WriteLine("#define {0}{1}", val[0],
				val.Length > 1 ? " " + val[1] : "");
			writer.WriteLine("#endif /* ndef {0} */", val[0]);
			writer.WriteLine();
		}

		readonly static Regex _includeRegex = new Regex(@"^(?<AutoHeader>ah:)?(?<Include>(""|<)(?<IncludeFile>.*)(""|>))$");

		protected static void WriteIncludeDeclaration(TextWriter writer, string inc)
		{
			if (inc == null || inc.Length == 0)
				return;
			Match m = _includeRegex.Match(inc);
			if (!m.Groups["Include"].Success)
			{
				Console.WriteLine("warning: invalid PublicIncludeFile: {0}", inc);
				return;
			}

			if (m.Success && m.Groups["AutoHeader"].Success)
			{
				string i = m.Groups["IncludeFile"].Value;
				string def = "HAVE_" + i.ToUpper().Replace("/", "_").Replace(".", "_");
				writer.WriteLine("#ifdef {0}", def);
				writer.WriteLine("#include {0}", m.Groups["Include"]);
				writer.WriteLine("#endif /* ndef {0} */", def);
			}
			else
				writer.WriteLine("#include {0}", m.Groups["Include"]);
		}

		protected string GetNativeMemberName(FieldInfo field)
		{
			if (!Configuration.MemberRenames.ContainsKey(field.Name))
				return field.Name;
			return Configuration.MemberRenames[field.Name];
		}
	}

	class HeaderFileGenerator : FileGenerator
	{
		StreamWriter _sh;
		string assembly_file;
		readonly Dictionary<string, MethodInfo> _methods = new Dictionary<string, MethodInfo>();
        readonly Dictionary<string, Type> _structs = new Dictionary<string, Type>();
        readonly Dictionary<string, MethodInfo> _delegates = new Dictionary<string, MethodInfo>();
        readonly List<string> _decls = new List<string>();

		public override void CreateFile(string assembly_name, string file_prefix)
		{
			_sh = File.CreateText(file_prefix + ".h");
			file_prefix = file_prefix.Replace("../", "").Replace("/", "_");
			this.assembly_file = assembly_name = Path.GetFileName(assembly_name);
			WriteHeader(_sh, assembly_name, true);
			assembly_name = assembly_name.Replace(".dll", "").Replace(".", "_");
			_sh.WriteLine("#ifndef INC_" + assembly_name + "_" + file_prefix + "_H");
			_sh.WriteLine("#define INC_" + assembly_name + "_" + file_prefix + "_H\n");
			_sh.WriteLine("#include <glib.h>\n");
			_sh.WriteLine("G_BEGIN_DECLS\n");

			// Kill warning about unused method
			DumpTypeInfo(null);
		}

		public override void WriteAssemblyAttributes(Assembly assembly)
		{
			_sh.WriteLine("/*\n * Public Macros\n */");
			foreach (string def in Configuration.PublicMacros)
				WriteMacroDefinition(_sh, def);

			_sh.WriteLine();

			_sh.WriteLine("/*\n * Public Includes\n */");
			foreach (string inc in Configuration.PublicHeaders)
				WriteIncludeDeclaration(_sh, inc);

			_sh.WriteLine();
			_sh.WriteLine("/*\n * Enumerations\n */");
		}

		public override void WriteType(Type t, string ns, string fn)
		{
			WriteEnum(t, ns, fn);
			CacheStructs(t, ns, fn);
			CacheExternalMethods(t, ns, fn);
		}

		void WriteEnum(Type t, string ns, string fn)
		{
			if (!CanMapType(t) || !t.IsEnum)
				return;

			string etype = MapUtils.GetNativeType(t);

			WriteLiteralValues(_sh, t, fn);
			_sh.WriteLine("int {1}_From{2} ({0} x, {0} *r);", etype, ns, t.Name);
			_sh.WriteLine("int {1}_To{2} ({0} x, {0} *r);", etype, ns, t.Name);
			Configuration.NativeExcludeSymbols.Add(
				string.Format("{1}_From{2}", etype, ns, t.Name));
			Configuration.NativeExcludeSymbols.Add(
				string.Format("{1}_To{2}", etype, ns, t.Name));
			Configuration.NativeExcludeSymbols.Sort();
			_sh.WriteLine();
		}

		static void WriteLiteralValues(StreamWriter sh, Type t, string n)
		{
			object inst = Activator.CreateInstance(t);
			int max_field_length = 0;
			FieldInfo[] fields = t.GetFields();
			Array.Sort(fields, delegate(FieldInfo f1, FieldInfo f2)
			{
				max_field_length = Math.Max(max_field_length, f1.Name.Length);
				max_field_length = Math.Max(max_field_length, f2.Name.Length);
				return MapUtils.MemberNameComparer.Compare(f1, f2);
			});
			max_field_length += 1 + n.Length;
			sh.WriteLine("enum {0} {{", n);
			foreach (FieldInfo fi in fields)
			{
				if (!fi.IsLiteral)
					continue;
				string e = n + "_" + fi.Name;
				sh.WriteLine("\t{0,-" + max_field_length + "}       = 0x{1:x},",
					e, fi.GetValue(inst));
				sh.WriteLine("\t#define {0,-" + max_field_length + "} {0}", e);
			}

			sh.WriteLine("};");
		}


		void CacheStructs(Type t, string ns, string fn)
		{
			if (t.IsEnum)
				return;
			MapAttribute map = MapUtils.GetMapAttribute(t);
			if (map != null)
			{
				if (map.NativeType != null && map.NativeType.Length > 0)
					_decls.Add(map.NativeType);
				RecordTypes(t);
			}
		}

		void CacheExternalMethods(Type t, string ns, string fn)
		{
			BindingFlags bf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
			foreach (MethodInfo m in t.GetMethods(bf))
			{
				if ((m.Attributes & MethodAttributes.PinvokeImpl) == 0)
					continue;
				DllImportAttribute dia = GetDllImportInfo(m);
				if (dia == null)
				{
					Console.WriteLine("warning: unable to emit native prototype for P/Invoke " +
					                  "method: {0}", m);
					continue;
				}

				// we shouldn't declare prototypes for POSIX, etc. functions.
				if (Configuration.NativeLibraries.BinarySearch(dia.Value) < 0 ||
				    IsOnExcludeList(dia.EntryPoint))
					continue;
				_methods[dia.EntryPoint] = m;
				RecordTypes(m);
			}
		}

		static DllImportAttribute GetDllImportInfo(MethodInfo method)
		{
			// .NET 2.0 synthesizes pseudo-attributes such as DllImport
			DllImportAttribute dia = MapUtils.GetCustomAttribute<DllImportAttribute>(method);
			if (dia != null)
				return dia;

			// We're not on .NET 2.0; assume we're on Mono and use some internal
			// methods...
			var MonoMethod = Type.GetType("System.Reflection.MonoMethod", false);
			if (MonoMethod == null)
			{
				Console.WriteLine("warning: cannot find MonoMethod");
				return null;
			}

			MethodInfo GetDllImportAttribute =
				MonoMethod.GetMethod("GetDllImportAttribute",
					BindingFlags.Static | BindingFlags.NonPublic);
			if (GetDllImportAttribute == null)
			{
				Console.WriteLine("warning: cannot find GetDllImportAttribute");
				return null;
			}

			IntPtr mhandle = method.MethodHandle.Value;
			return (DllImportAttribute) GetDllImportAttribute.Invoke(null,
				new object[] {mhandle});
		}

		bool IsOnExcludeList(string method)
		{
			int idx = Configuration.NativeExcludeSymbols.BinarySearch(method);
			return (idx < 0) ? false : true;
		}

		void RecordTypes(MethodInfo method)
		{
			ParameterInfo[] parameters = method.GetParameters();
			foreach (ParameterInfo pi in parameters)
				RecordTypes(pi.ParameterType);
		}

		void RecordTypes(Type st)
		{
			if (typeof(Delegate).IsAssignableFrom(st) && !_delegates.ContainsKey(st.Name))
			{
				MethodInfo mi = st.GetMethod("Invoke");
				_delegates[st.Name] = mi;
				RecordTypes(mi);
				return;
			}

			Type et = MapUtils.GetElementType(st);
			string s = MapUtils.GetNativeType(et);
			if (s.StartsWith("struct ") && !_structs.ContainsKey(et.FullName))
			{
				_structs[et.FullName] = et;
				foreach (FieldInfo fi in et.GetFields(BindingFlags.Instance |
				                                      BindingFlags.Public | BindingFlags.NonPublic))
				{
					RecordTypes(fi.FieldType);
				}
			}
		}

		public override void CloseFile(string file_prefix)
		{
			IEnumerable<string> structures = Sort(_structs.Keys);
			_sh.WriteLine();
			_sh.WriteLine("/*\n * Managed Structure Declarations\n */\n");
			foreach (string s in structures)
				_sh.WriteLine("struct {0};", MapUtils.GetManagedType(_structs[s]));

			_sh.WriteLine();

			_sh.WriteLine("/*\n * Inferred Structure Declarations\n */\n");
			foreach (string s in _decls)
				_sh.WriteLine("{0};", s);

			_sh.WriteLine();

			_sh.WriteLine("/*\n * Delegate Declarations\n */\n");
			foreach (string s in Sort(_delegates.Keys))
				_sh.WriteLine("typedef {0};",
					MapUtils.GetFunctionDeclaration("(*" + s + ")", _delegates[s]));

			_sh.WriteLine();

			_sh.WriteLine("/*\n * Structures\n */\n");
			foreach (string s in structures)
				WriteStructDeclarations(s);

			_sh.WriteLine();

			_sh.WriteLine("/*\n * Functions\n */");
			foreach (string method in Configuration.NativeExcludeSymbols)
			{
				if (_methods.ContainsKey(method))
					_methods.Remove(method);
			}

			foreach (string method in Sort(_methods.Keys))
				WriteMethodDeclaration((MethodInfo)_methods[method], method);

			_sh.WriteLine("\nG_END_DECLS\n");
			_sh.WriteLine("#endif /* ndef INC_Mono_Posix_" + file_prefix + "_H */\n");
			_sh.Close();
		}

		static IEnumerable<string> Sort(ICollection<string> c)
		{
			var al = new List<string>(c);
			al.Sort(MapUtils.OrdinalStringComparer);
			return al;
		}

		void WriteStructDeclarations(string s)
		{
			Type t = _structs[s];
#if false
		if (!t.Assembly.CodeBase.EndsWith (this.assembly_file)) {
			return;
		}
#endif
			_sh.WriteLine("struct {0} {{", MapUtils.GetManagedType(t));
			FieldInfo[] fields = t.GetFields(BindingFlags.Instance |
			                                 BindingFlags.Public | BindingFlags.NonPublic);
			int max_type_len = 0, max_name_len = 0, max_native_len = 0;
			Array.ForEach(fields, delegate(FieldInfo f)
			{
				max_type_len = Math.Max(max_type_len, HeaderFileGenerator.GetType(f.FieldType).Length);
				max_name_len = Math.Max(max_name_len, GetNativeMemberName(f).Length);
				string native_type = MapUtils.GetNativeType(f);
				if (native_type != null)
					max_native_len = Math.Max(max_native_len, native_type.Length);
			});
			SortFieldsInOffsetOrder(t, fields);
			foreach (FieldInfo field in fields)
			{
				string fname = GetNativeMemberName(field);
				_sh.Write("\t{0,-" + max_type_len + "} {1};",
					GetType(field.FieldType), fname);
				string native_type = MapUtils.GetNativeType(field);
				if (native_type != null)
				{
					_sh.Write(new string(' ', max_name_len - fname.Length));
					_sh.Write("  /* {0,-" + max_native_len + "} */", native_type);
				}

				_sh.WriteLine();
			}

			_sh.WriteLine("};");
			MapAttribute map = MapUtils.GetMapAttribute(t);
			if (map != null && map.NativeType != null && map.NativeType.Length != 0 &&
			    t.Assembly.CodeBase.EndsWith(this.assembly_file))
			{
				_sh.WriteLine();
				_sh.WriteLine(
					"int\n{0}_From{1} ({3}{4} from, {2} *to);\n" +
					"int\n{0}_To{1} ({2} *from, {3}{4} to);\n",
					MapUtils.GetNamespace(t), t.Name, map.NativeType,
					MapUtils.GetNativeType(t), t.IsValueType ? "*" : "");
				Configuration.NativeExcludeSymbols.Add(
					string.Format("{0}_From{1}", MapUtils.GetNamespace(t), t.Name));
				Configuration.NativeExcludeSymbols.Add(
					string.Format("{0}_To{1}", MapUtils.GetNamespace(t), t.Name));
				Configuration.NativeExcludeSymbols.Sort();
			}

			_sh.WriteLine();
		}

		static string GetType(Type t)
		{
			if (typeof(Delegate).IsAssignableFrom(t))
				return t.Name;
			return MapUtils.GetNativeType(t);
		}

		void WriteMethodDeclaration(MethodInfo method, string entryPoint)
		{
			if (method.ReturnType.IsClass)
			{
				Console.WriteLine("warning: {0} has a return type of {1}, which is a reference type",
					entryPoint, method.ReturnType.FullName);
			}

			_sh.Write(MapUtils.GetFunctionDeclaration(entryPoint, method));
			_sh.WriteLine(";");
		}

		void DumpTypeInfo(Type t)
		{
			if (t == null)
				return;

			_sh.WriteLine("\t\t/* Type Info for " + t.FullName + ":");
			foreach (MemberInfo mi in typeof(Type).GetMembers())
				_sh.WriteLine("\t\t\t{0}={1}", mi.Name, GetMemberValue(mi, t));

			_sh.WriteLine("\t\t */");
		}

		static string GetMemberValue(MemberInfo mi, Type t)
		{
			try
			{
				switch (mi.MemberType)
				{
					case MemberTypes.Constructor:
					case MemberTypes.Method:
					{
						var b = (MethodBase) mi;
						if (b.GetParameters().Length == 0)
							return b.Invoke(t, new object[] { }).ToString();
						return "<<cannot invoke>>";
					}
					case MemberTypes.Field:
						return ((FieldInfo) mi).GetValue(t).ToString();
					case MemberTypes.Property:
					{
						var pi = (PropertyInfo) mi;
						if (!pi.CanRead)
							return "<<cannot read>>";
						return pi.GetValue(t, null).ToString();
					}
					default:
						return "<<unknown value>>";
				}
			}
			catch (Exception e)
			{
				return "<<exception reading member: " + e.Message + ">>";
			}
		}
	}

	class SourceFileGenerator : FileGenerator
	{
		StreamWriter _sc;
		string file_prefix;

		public override void CreateFile(string assembly_name, string file_prefix)
		{
			_sc = File.CreateText(file_prefix + ".c");
			WriteHeader(_sc, assembly_name);

			if (file_prefix.IndexOf("/") != -1)
				file_prefix = file_prefix.Substring(file_prefix.IndexOf("/") + 1);
			this.file_prefix = file_prefix;
			_sc.WriteLine("#include <stdlib.h>");
			_sc.WriteLine("#include <string.h>");
			_sc.WriteLine();
		}

		public override void WriteAssemblyAttributes(Assembly assembly)
		{
			_sc.WriteLine("/*\n * Implementation Macros\n */");
			foreach (string def in Configuration.ImplementationMacros)
				WriteMacroDefinition(_sc, def);

			_sc.WriteLine();

			_sc.WriteLine("/*\n * Implementation Includes\n */");
			foreach (string inc in Configuration.ImplementationHeaders)
				WriteIncludeDeclaration(_sc, inc);

			_sc.WriteLine();

			_sc.WriteLine("#include \"{0}.h\"", file_prefix);

			_sc.WriteLine(@"
#include <errno.h>    /* errno, EOVERFLOW */
#include <glib.h>     /* g* types, g_assert_not_reached() */");

			WriteFallbackMacro("CNM_MININT8", "G_MININT8", sbyte.MinValue.ToString());
			WriteFallbackMacro("CNM_MAXINT8", "G_MAXINT8", sbyte.MaxValue.ToString());
			WriteFallbackMacro("CNM_MAXUINT8", "G_MAXUINT8", byte.MaxValue.ToString());
			WriteFallbackMacro("CNM_MININT16", "G_MININT16", short.MinValue.ToString());
			WriteFallbackMacro("CNM_MAXINT16", "G_MAXINT16", short.MaxValue.ToString());
			WriteFallbackMacro("CNM_MAXUINT16", "G_MAXUINT16", ushort.MaxValue.ToString());
			WriteFallbackMacro("CNM_MININT32", "G_MININT32", int.MinValue.ToString());
			WriteFallbackMacro("CNM_MAXINT32", "G_MAXINT32", int.MaxValue.ToString());
			WriteFallbackMacro("CNM_MAXUINT32", "G_MAXUINT32", uint.MaxValue.ToString() + "U");
			WriteFallbackMacro("CNM_MININT64", "G_MININT64", long.MinValue.ToString() + "LL");
			WriteFallbackMacro("CNM_MAXINT64", "G_MAXINT64", long.MaxValue.ToString() + "LL");
			WriteFallbackMacro("CNM_MAXUINT64", "G_MAXUINT64", ulong.MaxValue.ToString() + "ULL");

			_sc.WriteLine(@"

/* returns TRUE if @type is an unsigned type */
#define _cnm_integral_type_is_unsigned(type) \
    (sizeof(type) == sizeof(gint8)           \
      ? (((type)-1) > CNM_MAXINT8)             \
      : sizeof(type) == sizeof(gint16)       \
        ? (((type)-1) > CNM_MAXINT16)          \
        : sizeof(type) == sizeof(gint32)     \
          ? (((type)-1) > CNM_MAXINT32)        \
          : sizeof(type) == sizeof(gint64)   \
            ? (((type)-1) > CNM_MAXINT64)      \
            : (g_assert_not_reached (), 0))

/* returns the minimum value of @type as a gint64 */
#define _cnm_integral_type_min(type)          \
    (_cnm_integral_type_is_unsigned (type)    \
      ? 0                                     \
      : sizeof(type) == sizeof(gint8)         \
        ? CNM_MININT8                           \
        : sizeof(type) == sizeof(gint16)      \
          ? CNM_MININT16                        \
          : sizeof(type) == sizeof(gint32)    \
            ? CNM_MININT32                      \
            : sizeof(type) == sizeof(gint64)  \
              ? CNM_MININT64                    \
              : (g_assert_not_reached (), 0))

/* returns the maximum value of @type as a guint64 */
#define _cnm_integral_type_max(type)            \
    (_cnm_integral_type_is_unsigned (type)      \
      ? sizeof(type) == sizeof(gint8)           \
        ? CNM_MAXUINT8                            \
        : sizeof(type) == sizeof(gint16)        \
          ? CNM_MAXUINT16                         \
          : sizeof(type) == sizeof(gint32)      \
            ? CNM_MAXUINT32                       \
            : sizeof(type) == sizeof(gint64)    \
              ? CNM_MAXUINT64                     \
              : (g_assert_not_reached (), 0)    \
      : sizeof(type) == sizeof(gint8)           \
          ? CNM_MAXINT8                           \
          : sizeof(type) == sizeof(gint16)      \
            ? CNM_MAXINT16                        \
            : sizeof(type) == sizeof(gint32)    \
              ? CNM_MAXINT32                      \
              : sizeof(type) == sizeof(gint64)  \
                ? CNM_MAXINT64                    \
                : (g_assert_not_reached (), 0))

#ifdef _CNM_DUMP
#define _cnm_dump(to_t,from)                                             \
  printf (""# %s -> %s: uns=%i; min=%llx; max=%llx; value=%llx; lt=%i; l0=%i; gt=%i; e=%i\n"", \
    #from, #to_t,                                                        \
    (int) _cnm_integral_type_is_unsigned (to_t),                         \
    (gint64) (_cnm_integral_type_min (to_t)),                            \
    (gint64) (_cnm_integral_type_max (to_t)),                            \
    (gint64) (from),                                                     \
    (((gint64) _cnm_integral_type_min (to_t)) <= (gint64) from),         \
    (from < 0),                                                          \
    (((guint64) from) <= (guint64) _cnm_integral_type_max (to_t)),       \
    !((int) _cnm_integral_type_is_unsigned (to_t)                        \
      ? ((0 <= from) &&                                                  \
         ((guint64) from <= (guint64) _cnm_integral_type_max (to_t)))    \
      : ((gint64) _cnm_integral_type_min(to_t) <= (gint64) from &&       \
         (guint64) from <= (guint64) _cnm_integral_type_max (to_t)))     \
  )
#else /* ndef _CNM_DUMP */
#define _cnm_dump(to_t, from) do {} while (0)
#endif /* def _CNM_DUMP */

#ifdef DEBUG
#define _cnm_return_val_if_overflow(to_t,from,val)  G_STMT_START {   \
    int     uns = _cnm_integral_type_is_unsigned (to_t);             \
    gint64  min = (gint64)  _cnm_integral_type_min (to_t);           \
    guint64 max = (guint64) _cnm_integral_type_max (to_t);           \
    gint64  sf  = (gint64)  from;                                    \
    guint64 uf  = (guint64) from;                                    \
    if (!(uns ? ((0 <= from) && (uf <= max))                         \
              : (min <= sf && (from < 0 || uf <= max)))) {           \
      _cnm_dump(to_t, from);                                         \
      errno = EOVERFLOW;                                             \
      return (val);                                                  \
    }                                                                \
  } G_STMT_END
#else /* !def DEBUG */
/* don't do any overflow checking */
#define _cnm_return_val_if_overflow(to_t,from,val)  G_STMT_START {   \
  } G_STMT_END
#endif /* def DEBUG */
");
		}

		void WriteFallbackMacro(string target, string glib, string def)
		{
			_sc.WriteLine(@"
#if defined ({1})
#define {0} {1}
#else
#define {0} ({2})
#endif", target, glib, def);
		}

		public override void WriteType(Type t, string ns, string fn)
		{
			if (!CanMapType(t))
				return;

			string etype = MapUtils.GetNativeType(t);

			if (t.IsEnum)
			{
				bool bits = IsFlagsEnum(t);

				WriteFromManagedEnum(t, ns, fn, etype, bits);
				WriteToManagedEnum(t, ns, fn, etype, bits);
			}
			else
			{
				WriteFromManagedClass(t, ns, fn, etype);
				WriteToManagedClass(t, ns, fn, etype);
			}
		}

		void WriteFromManagedEnum(Type t, string ns, string fn, string etype, bool bits)
		{
			_sc.WriteLine("int {1}_From{2} ({0} x, {0} *r)", etype, ns, t.Name);
			_sc.WriteLine("{");
			_sc.WriteLine("\t*r = 0;");
			FieldInfo[] fields = t.GetFields();
			Array.Sort<FieldInfo>(fields, MapUtils.MemberNameComparer);
			Array values = Enum.GetValues(t);
			foreach (FieldInfo fi in fields)
			{
				if (!fi.IsLiteral)
					continue;
				if (MapUtils.GetCustomAttribute<ObsoleteAttribute>(fi) != null)
				{
					_sc.WriteLine("\t/* {0}_{1} is obsolete or optional; ignoring */", fn, fi.Name);
					continue;
				}

				MapAttribute map = MapUtils.GetMapAttribute(fi);
				bool is_bits = bits && (map != null ? map.SuppressFlags == null : true);
				if (is_bits)
					// properly handle case where [Flags] enumeration has helper
					// synonyms.  e.g. DEFFILEMODE and ACCESSPERMS for mode_t.
					_sc.WriteLine("\tif ((x & {0}_{1}) == {0}_{1})", fn, fi.Name);
				else if (GetSuppressFlags(map) == null)
					_sc.WriteLine("\tif (x == {0}_{1})", fn, fi.Name);
				else
					_sc.WriteLine("\tif ((x & {0}_{1}) == {0}_{2})", fn, map.SuppressFlags, fi.Name);
				_sc.WriteLine("#ifdef {0}", fi.Name);
				if (is_bits || GetSuppressFlags(map) != null)
					_sc.WriteLine("\t\t*r |= {1};", fn, fi.Name);
				else
					_sc.WriteLine("\t\t{{*r = {1}; return 0;}}", fn, fi.Name);
				_sc.WriteLine("#else /* def {0} */", fi.Name);
				if (is_bits && IsRedundant(t, fi, values))
				{
					_sc.WriteLine("\t\t{{/* Ignoring {0}_{1}, as it is constructed from other values */}}",
						fn, fi.Name);
				}
				else
				{
					_sc.WriteLine("\t\t{errno = EINVAL; return -1;}");
				}

				_sc.WriteLine("#endif /* ndef {0} */", fi.Name);
			}

			// For many values, 0 is a valid value, but doesn't have it's own symbol.
			// Examples: Error (0 means "no error"), WaitOptions (0 means "no options").
			// Make 0 valid for all conversions.
			_sc.WriteLine("\tif (x == 0)\n\t\treturn 0;");
			if (bits)
				_sc.WriteLine("\treturn 0;");
			else
				_sc.WriteLine("\terrno = EINVAL; return -1;"); // return error if not matched
			_sc.WriteLine("}\n");
		}

		static string GetSuppressFlags(MapAttribute map)
		{
			if (map != null)
			{
				return map.SuppressFlags == null
					? null
					: map.SuppressFlags.Length == 0
						? null
						: map.SuppressFlags;
			}

			return null;
		}

		static bool IsRedundant(Type t, FieldInfo fi, Array values)
		{
			long v = Convert.ToInt64(fi.GetValue(null));
			long d = v;
			if (v == 0)
				return false;
			foreach (object o in values)
			{
				long e = Convert.ToInt64(o);
				if (((d & e) != 0) && (e < d))
				{
					v &= ~e;
				}
			}

			if (v == 0)
			{
				return true;
			}

			return false;
		}

		void WriteToManagedEnum(Type t, string ns, string fn, string etype, bool bits)
		{
			_sc.WriteLine("int {1}_To{2} ({0} x, {0} *r)", etype, ns, t.Name);
			_sc.WriteLine("{");
			_sc.WriteLine("\t*r = 0;", etype);
			// For many values, 0 is a valid value, but doesn't have it's own symbol.
			// Examples: Error (0 means "no error"), WaitOptions (0 means "no options").
			// Make 0 valid for all conversions.
			_sc.WriteLine("\tif (x == 0)\n\t\treturn 0;");
			FieldInfo[] fields = t.GetFields();
			Array.Sort<FieldInfo>(fields, MapUtils.MemberNameComparer);
			foreach (FieldInfo fi in fields)
			{
				if (!fi.IsLiteral)
					continue;
				MapAttribute map = MapUtils.GetMapAttribute(fi);
				bool is_bits = bits && (map != null ? map.SuppressFlags == null : true);
				_sc.WriteLine("#ifdef {0}", fi.Name);
				if (is_bits)
					// properly handle case where [Flags] enumeration has helper
					// synonyms.  e.g. DEFFILEMODE and ACCESSPERMS for mode_t.
					_sc.WriteLine("\tif ((x & {1}) == {1})\n\t\t*r |= {0}_{1};", fn, fi.Name);
				else if (GetSuppressFlags(map) == null)
					_sc.WriteLine("\tif (x == {1})\n\t\t{{*r = {0}_{1}; return 0;}}", fn, fi.Name);
				else
					_sc.WriteLine("\tif ((x & {2}) == {1})\n\t\t*r |= {0}_{1};", fn, fi.Name, map.SuppressFlags);
				_sc.WriteLine("#endif /* ndef {0} */", fi.Name);
			}

			if (bits)
				_sc.WriteLine("\treturn 0;");
			else
				_sc.WriteLine("\terrno = EINVAL; return -1;");
			_sc.WriteLine("}\n");
		}

		void WriteFromManagedClass(Type t, string ns, string fn, string etype)
		{
			MapAttribute map = MapUtils.GetMapAttribute(t);
			if (map == null || map.NativeType == null || map.NativeType.Length == 0)
				return;
			string nativeMacro = GetAutoconfDefine(map.NativeType);
			_sc.WriteLine("#ifdef {0}", nativeMacro);
			_sc.WriteLine("int\n{0}_From{1} (struct {0}_{1} *from, {2} *to)",
				MapUtils.GetNamespace(t), t.Name, map.NativeType);
			WriteManagedClassConversion(t, delegate(FieldInfo field)
				{
					MapAttribute ft = MapUtils.GetMapAttribute(field);
					if (ft != null)
						return ft.NativeType;
					return MapUtils.GetNativeType(field.FieldType);
				},
				delegate(FieldInfo field) { return GetNativeMemberName(field); },
				delegate(FieldInfo field) { return field.Name; },
				delegate(FieldInfo field)
				{
					return string.Format("{0}_From{1}",
						MapUtils.GetNamespace(field.FieldType),
						field.FieldType.Name);
				}
			);
			_sc.WriteLine("#endif /* ndef {0} */\n\n", nativeMacro);
		}

		static string GetAutoconfDefine(string nativeType)
		{
			return string.Format("HAVE_{0}",
				nativeType.ToUpperInvariant().Replace(" ", "_"));
		}

		delegate string GetFromType(FieldInfo field);
		delegate string GetToFieldName(FieldInfo field);
		delegate string GetFromFieldName(FieldInfo field);
		delegate string GetFieldCopyMethod(FieldInfo field);

		void WriteManagedClassConversion(Type t, GetFromType gft,
			GetFromFieldName gffn, GetToFieldName gtfn, GetFieldCopyMethod gfc)
		{
			MapAttribute map = MapUtils.GetMapAttribute(t);
			_sc.WriteLine("{");
			FieldInfo[] fields = GetFieldsToCopy(t);
			SortFieldsInOffsetOrder(t, fields);
			int max_len = 0;
			foreach (FieldInfo f in fields)
			{
				max_len = Math.Max(max_len, f.Name.Length);
				if (!MapUtils.IsIntegralType(f.FieldType))
					continue;
				string d = GetAutoconfDefine(map, f);
				if (d != null)
					_sc.WriteLine("#ifdef " + d);
				_sc.WriteLine("\t_cnm_return_val_if_overflow ({0}, from->{1}, -1);",
					gft(f), gffn(f));
				if (d != null)
					_sc.WriteLine("#endif /* ndef " + d + " */");
			}

			_sc.WriteLine("\n\tmemset (to, 0, sizeof(*to));\n");
			foreach (FieldInfo f in fields)
			{
				string d = GetAutoconfDefine(map, f);
				if (d != null)
					_sc.WriteLine("#ifdef " + d);
				if (MapUtils.IsBlittableType(f.FieldType))
				{
					_sc.WriteLine("\tto->{0,-" + max_len + "} = from->{1};",
						gtfn(f), gffn(f));
				}
				else if (f.FieldType.IsEnum)
				{
					_sc.WriteLine("\tif ({0} (from->{1}, &to->{2}) != 0) {{", gfc(f),
						gffn(f), gtfn(f));
					_sc.WriteLine("\t\treturn -1;");
					_sc.WriteLine("\t}");
				}
				else if (f.FieldType.IsValueType)
				{
					_sc.WriteLine("\tif ({0} (&from->{1}, &to->{2}) != 0) {{", gfc(f),
						gffn(f), gtfn(f));
					_sc.WriteLine("\t\treturn -1;");
					_sc.WriteLine("\t}");
				}

				if (d != null)
					_sc.WriteLine("#endif /* ndef " + d + " */");
			}

			_sc.WriteLine();
			_sc.WriteLine("\treturn 0;");
			_sc.WriteLine("}");
		}

		void WriteToManagedClass(Type t, string ns, string fn, string etype)
		{
			MapAttribute map = MapUtils.GetMapAttribute(t);
			if (map == null || map.NativeType == null || map.NativeType.Length == 0)
				return;
			string nativeMacro = GetAutoconfDefine(map.NativeType);
			_sc.WriteLine("#ifdef {0}", nativeMacro);
			_sc.WriteLine("int\n{0}_To{1} ({2} *from, struct {0}_{1} *to)",
				MapUtils.GetNamespace(t), t.Name, map.NativeType);
			WriteManagedClassConversion(t, delegate(FieldInfo field) { return MapUtils.GetNativeType(field.FieldType); },
				delegate(FieldInfo field) { return field.Name; },
				delegate(FieldInfo field) { return GetNativeMemberName(field); },
				delegate(FieldInfo field)
				{
					return string.Format("{0}_To{1}",
						MapUtils.GetNamespace(field.FieldType),
						field.FieldType.Name);
				}
			);
			_sc.WriteLine("#endif /* ndef {0} */\n\n", nativeMacro);
		}

		static FieldInfo[] GetFieldsToCopy(Type t)
		{
			FieldInfo[] fields = t.GetFields(BindingFlags.Instance |
			                                 BindingFlags.Public | BindingFlags.NonPublic);
			int count = 0;
			for (int i = 0; i < fields.Length; ++i)
				if (MapUtils.GetCustomAttribute<NonSerializedAttribute>(fields[i]) == null)
					++count;
			FieldInfo[] rf = new FieldInfo [count];
			for (int i = 0, j = 0; i < fields.Length; ++i)
			{
				if (MapUtils.GetCustomAttribute<NonSerializedAttribute>(fields[i]) == null)
					rf[j++] = fields[i];
			}

			return rf;
		}

		string GetAutoconfDefine(MapAttribute typeMap, FieldInfo field)
		{
			if (Configuration.AutoconfMembers.BinarySearch(field.Name) < 0 &&
			    Configuration.AutoconfMembers.BinarySearch(field.DeclaringType.Name + "." + field.Name) < 0)
				return null;
			return string.Format("HAVE_{0}_{1}",
				typeMap.NativeType.ToUpperInvariant().Replace(" ", "_"),
				field.Name.ToUpperInvariant());
		}

		public override void CloseFile(string file_prefix)
		{
			_sc.Close();
		}
	}

	class ConvertFileGenerator : FileGenerator
	{
		StreamWriter _scs;

		public override void CreateFile(string assembly_name, string file_prefix)
		{
			_scs = File.CreateText(file_prefix + ".cs");
			WriteHeader(_scs, assembly_name, true);
			_scs.WriteLine("using System;");
			_scs.WriteLine("using System.Runtime.InteropServices;");
			_scs.WriteLine("using Mono.Unix.Native;\n");
			_scs.WriteLine("namespace Mono.Unix.Native {\n");
			_scs.WriteLine("\tpublic sealed /* static */ partial class NativeConvert");
			_scs.WriteLine("\t{");
			_scs.WriteLine("\t\tprivate NativeConvert () {}\n");
			_scs.WriteLine("\t\tprivate const string LIB = \"{0}\";\n", Configuration.NativeLibraries[0]);
			_scs.WriteLine("\t\tstatic void ThrowArgumentException (object value)");
			_scs.WriteLine("\t\t{");
			_scs.WriteLine("\t\t\tthrow new ArgumentOutOfRangeException (\"value\", value,");
			_scs.WriteLine("\t\t\t\tLocale.GetText (\"Current platform doesn't support this value.\"));");
			_scs.WriteLine("\t\t}\n");
		}

		public override void WriteType(Type t, string ns, string fn)
		{
			if (!CanMapType(t))
				return;
			if (t.IsEnum)
				WriteEnum(t, ns, fn);
			else
				WriteStruct(t, ns, fn);
		}

		void WriteEnum(Type t, string ns, string fn)
		{
			string mtype = Enum.GetUnderlyingType(t).Name;
			ObsoleteAttribute oa = MapUtils.GetCustomAttribute<ObsoleteAttribute>(t);
			string obsolete = "";
			if (oa != null)
			{
				obsolete = string.Format("[Obsolete (\"{0}\", {1})]\n\t\t",
					oa.Message, oa.IsError ? "true" : "false");
			}

			_scs.WriteLine(
				"\t\t{0}[DllImport (LIB, EntryPoint=\"{1}_From{2}\")]\n" +
				"\t\tstatic extern int From{2} ({2} value, out {3} rval);\n" +
				"\n" +
				"\t\t{0}public static bool TryFrom{2} ({2} value, out {3} rval)\n" +
				"\t\t{{\n" +
				"\t\t\treturn From{2} (value, out rval) == 0;\n" +
				"\t\t}}\n" +
				"\n" +
				"\t\t{0}public static {3} From{2} ({2} value)\n" +
				"\t\t{{\n" +
				"\t\t\t{3} rval;\n" +
				"\t\t\tif (From{2} (value, out rval) == -1)\n" +
				"\t\t\t\tThrowArgumentException (value);\n" +
				"\t\t\treturn rval;\n" +
				"\t\t}}\n" +
				"\n" +
				"\t\t{0}[DllImport (LIB, EntryPoint=\"{1}_To{2}\")]\n" +
				"\t\tstatic extern int To{2} ({3} value, out {2} rval);\n" +
				"\n" +
				"\t\t{0}public static bool TryTo{2} ({3} value, out {2} rval)\n" +
				"\t\t{{\n" +
				"\t\t\treturn To{2} (value, out rval) == 0;\n" +
				"\t\t}}\n" +
				"\n" +
				"\t\t{0}public static {2} To{2} ({3} value)\n" +
				"\t\t{{\n" +
				"\t\t\t{2} rval;\n" +
				"\t\t\tif (To{2} (value, out rval) == -1)\n" +
				"\t\t\t\tThrowArgumentException (value);\n" +
				"\t\t\treturn rval;\n" +
				"\t\t}}\n",
				obsolete, ns, t.Name, mtype
			);
		}

		void WriteStruct(Type t, string ns, string fn)
		{
			if (MapUtils.IsDelegate(t))
				return;
			MapAttribute map = MapUtils.GetMapAttribute(t);
			if (map == null || map.NativeType == null || map.NativeType.Length == 0)
				return;
			ObsoleteAttribute oa = MapUtils.GetCustomAttribute<ObsoleteAttribute>(t);
			string obsolete = "";
			if (oa != null)
			{
				obsolete = string.Format("[Obsolete (\"{0}\", {1})]\n\t\t",
					oa.Message, oa.IsError ? "true" : "false");
			}

			string _ref = t.IsValueType ? "ref " : "";
			string _out = t.IsValueType ? "out " : "";
			_scs.WriteLine(
				"\t\t{0}[DllImport (LIB, EntryPoint=\"{1}_From{2}\")]\n" +
				"\t\tstatic extern int From{2} ({3}{2} source, IntPtr destination);\n" +
				"\n" +
				"\t\t{0}public static bool TryCopy ({3}{2} source, IntPtr destination)\n" +
				"\t\t{{\n" +
				"\t\t\treturn From{2} ({3}source, destination) == 0;\n" +
				"\t\t}}\n" +
				"\n" +
				"\t\t{0}[DllImport (LIB, EntryPoint=\"{1}_To{2}\")]\n" +
				"\t\tstatic extern int To{2} (IntPtr source, {4}{2} destination);\n" +
				"\n" +
				"\t\t{0}public static bool TryCopy (IntPtr source, {4}{2} destination)\n" +
				"\t\t{{\n" +
				"\t\t\treturn To{2} (source, {4}destination) == 0;\n" +
				"\t\t}}\n",
				obsolete, ns, t.Name, _ref, _out
			);
		}

		public override void CloseFile(string file_prefix)
		{
			_scs.WriteLine("\t}");
			_scs.WriteLine("}\n");
			_scs.Close();
		}
	}

	class ConvertDocFileGenerator : FileGenerator
	{
		StreamWriter _scs;

		public override void CreateFile(string assembly_name, string file_prefix)
		{
			_scs = File.CreateText(file_prefix + ".xml");
			_scs.WriteLine("    <!-- BEGIN GENERATED CONTENT");
			WriteHeader(_scs, assembly_name, true);
			_scs.WriteLine("      -->");
		}

		public override void WriteType(Type t, string ns, string fn)
		{
			if (!CanMapType(t) || !t.IsEnum)
				return;

			bool bits = IsFlagsEnum(t);

			string type = GetCSharpType(t);
			string mtype = Enum.GetUnderlyingType(t).FullName;
			string member = t.Name;
			string ftype = t.FullName;

			string to_returns = "";
			string to_remarks = "";
			string to_exception = "";

			if (bits)
			{
				to_returns = "<returns>An approximation of the equivalent managed value.</returns>";
				to_remarks = @"<para>The current conversion functions are unable to determine
        if a value in a <c>[Flags]</c>-marked enumeration <i>does not</i> 
        exist on the current platform.  As such, if <paramref name=""value"" /> 
        contains a flag value which the current platform doesn't support, it 
        will not be present in the managed value returned.</para>
        <para>This should only be a problem if <paramref name=""value"" /> 
        <i>was not</i> previously returned by 
        <see cref=""M:Mono.Unix.Native.NativeConvert.From" + member + "\" />.</para>\n";
			}
			else
			{
				to_returns = "<returns>The equivalent managed value.</returns>";
				to_exception = @"
        <exception cref=""T:System.ArgumentOutOfRangeException"">
          <paramref name=""value"" /> has no equivalent managed value.
        </exception>
";
			}

			_scs.WriteLine(@"
    <Member MemberName=""TryFrom{1}"">
      <MemberSignature Language=""C#"" Value=""public static bool TryFrom{1} ({0} value, out {2} rval);"" />
      <MemberType>Method</MemberType>
      <ReturnValue>
        <ReturnType>System.Boolean</ReturnType>
      </ReturnValue>
      <Parameters>
        <Parameter Name=""value"" Type=""{0}"" />
        <Parameter Name=""rval"" Type=""{3}&amp;"" RefType=""out"" />
      </Parameters>
      <Docs>
        <param name=""value"">The managed value to convert.</param>
        <param name=""rval"">The OS-specific equivalent value.</param>
        <summary>Converts a <see cref=""T:{0}"" /> 
          enumeration value to an OS-specific value.</summary>
        <returns><see langword=""true"" /> if the conversion was successful; 
        otherwise, <see langword=""false"" />.</returns>
        <remarks><para>This is an exception-safe alternative to 
        <see cref=""M:Mono.Unix.Native.NativeConvert.From{1}"" />.</para>
        <para>If successful, this method stores the OS-specific equivalent
        value of <paramref name=""value"" /> into <paramref name=""rval"" />.
        Otherwise, <paramref name=""rval"" /> will contain <c>0</c>.</para>
        </remarks>
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.From{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.To{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.TryTo{1}"" />
      </Docs>
    </Member>
    <Member MemberName=""From{1}"">
      <MemberSignature Language=""C#"" Value=""public static {2} From{1} ({0} value);"" />
      <MemberType>Method</MemberType>
      <ReturnValue>
        <ReturnType>{3}</ReturnType>
      </ReturnValue>
      <Parameters>
        <Parameter Name=""value"" Type=""{0}"" />
      </Parameters>
      <Docs>
        <param name=""value"">The managed value to convert.</param>
        <summary>Converts a <see cref=""T:{0}"" /> 
          to an OS-specific value.</summary>
        <returns>The equivalent OS-specific value.</returns>
        <exception cref=""T:System.ArgumentOutOfRangeException"">
          <paramref name=""value"" /> has no equivalent OS-specific value.
        </exception>
        <remarks></remarks>
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.To{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.TryFrom{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.TryTo{1}"" />
      </Docs>
    </Member>
    <Member MemberName=""TryTo{1}"">
      <MemberSignature Language=""C#"" Value=""public static bool TryTo{1} ({2} value, out {0} rval);"" />
      <MemberType>Method</MemberType>
      <ReturnValue>
        <ReturnType>System.Boolean</ReturnType>
      </ReturnValue>
      <Parameters>
        <Parameter Name=""value"" Type=""{3}"" />
        <Parameter Name=""rval"" Type=""{0}&amp;"" RefType=""out"" />
      </Parameters>
      <Docs>
        <param name=""value"">The OS-specific value to convert.</param>
        <param name=""rval"">The managed equivalent value</param>
        <summary>Converts an OS-specific value to a 
          <see cref=""T:{0}"" />.</summary>
        <returns><see langword=""true"" /> if the conversion was successful; 
        otherwise, <see langword=""false"" />.</returns>
        <remarks><para>This is an exception-safe alternative to 
        <see cref=""M:Mono.Unix.Native.NativeConvert.To{1}"" />.</para>
        <para>If successful, this method stores the managed equivalent
        value of <paramref name=""value"" /> into <paramref name=""rval"" />.
        Otherwise, <paramref name=""rval"" /> will contain a <c>0</c>
        cast to a <see cref=""T:{0}"" />.</para>
        " + to_remarks +
			              @"        </remarks>
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.From{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.To{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.TryFrom{1}"" />
      </Docs>
    </Member>
    <Member MemberName=""To{1}"">
      <MemberSignature Language=""C#"" Value=""public static {0} To{1} ({2} value);"" />
      <MemberType>Method</MemberType>
      <ReturnValue>
        <ReturnType>{0}</ReturnType>
      </ReturnValue>
      <Parameters>
        <Parameter Name=""value"" Type=""{3}"" />
      </Parameters>
      <Docs>
        <param name=""value"">The OS-specific value to convert.</param>
        <summary>Converts an OS-specific value to a 
          <see cref=""T:{0}"" />.</summary>
					" + to_returns + "\n" +
			              to_exception +
			              @"        <remarks>
        " + to_remarks + @"
        </remarks>
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.From{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.TryFrom{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.TryTo{1}"" />
      </Docs>
    </Member>
", ftype, member, type, mtype
			);
		}

		string GetCSharpType(Type t)
		{
			string ut = t.Name;
			if (t.IsEnum)
				ut = Enum.GetUnderlyingType(t).Name;
			Type et = t.GetElementType();
			if (et != null && et.IsEnum)
				ut = Enum.GetUnderlyingType(et).Name;

			string type = null;

			switch (ut)
			{
				case "Boolean":
					type = "bool";
					break;
				case "Byte":
					type = "byte";
					break;
				case "SByte":
					type = "sbyte";
					break;
				case "Int16":
					type = "short";
					break;
				case "UInt16":
					type = "ushort";
					break;
				case "Int32":
					type = "int";
					break;
				case "UInt32":
					type = "uint";
					break;
				case "Int64":
					type = "long";
					break;
				case "UInt64":
					type = "ulong";
					break;
			}

			return type;
		}

		public override void CloseFile(string file_prefix)
		{
			_scs.WriteLine("    <!-- END GENERATED CONTENT -->");
			_scs.Close();
		}
	}
}
// vim: noexpandtab