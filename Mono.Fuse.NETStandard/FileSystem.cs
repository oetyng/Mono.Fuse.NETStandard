//
// Mono.Fuse/FileSystem.cs
//
// Authors:
//   Jonathan Pryor (jonpryor@vt.edu)
//
// (C) 2006-2007 Jonathan Pryor
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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Unix;
using Mono.Unix.Native;

namespace Mono.Fuse.NETStandard {

	[StructLayout(LayoutKind.Sequential)]
	public sealed class FileSystemOperationContext
    {
		internal IntPtr fuse;
		[Map("uid_t")] private long userId;
		[Map("gid_t")] private long groupId;
		[Map("pid_t")] int  processId;

		internal FileSystemOperationContext()
		{ }

        public long UserId => userId;
        public long GroupId => groupId;
        public int ProcessId => processId;
    }

	public class DirectoryEntry
    {
        readonly static char[] _invalidPathChars = new char[] { '/' };

        public string Name { get; }

        // This is used only if st_ino is non-zero and
        // FileSystem.SetsInodes is true
        public Stat Stat;

		public DirectoryEntry (string name)
		{
			if (name == null)
				throw new ArgumentNullException ("name");
			if (name.IndexOfAny (_invalidPathChars) != -1)
				throw new ArgumentException ("name cannot contain directory separator char", "name");
			Name = name;
		}
	}

	[Map]
	[StructLayout(LayoutKind.Sequential)]
	public sealed class OpenedPathInfo
    {
		internal OpenFlags flags;
		int   write_page;
		private bool  direct_io;
		private bool  keep_cache;
		private ulong file_handle;

		internal OpenedPathInfo()
		{ }

		public OpenFlags OpenFlags
        {
			get { return flags; }
			set { flags = value; }
		}

		private const OpenFlags accessMask = 
			OpenFlags.O_RDONLY | OpenFlags.O_WRONLY | OpenFlags.O_RDWR;

		public OpenFlags OpenAccess => flags & accessMask;

		public int WritePage
        {
			get { return write_page; }
			set { write_page = value; }
		}

		public bool DirectIO
        {
			get { return direct_io; }
			set { direct_io = value; }
		}

		public bool KeepCache
        {
			get { return keep_cache; }
			set { keep_cache = value; }
		}

		public IntPtr Handle
        {
			get { return (IntPtr)(long)file_handle; }
			set { file_handle = (ulong)(long) value; }
		}
	}

	delegate int GetPathStatusCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, IntPtr stat);
	delegate int ReadSymbolicLinkCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, IntPtr buf, ulong bufsize);
	delegate int CreateSpecialFileCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, uint perms, ulong dev);
	delegate int CreateDirectoryCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, uint mode);
	delegate int RemoveFileCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path);
	delegate int RemoveDirectoryCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path);
	delegate int CreateSymbolicLinkCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string oldpath, 
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string newpath);
	delegate int RenamePathCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string oldpath, 
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string newpath);
	delegate int CreateHardLinkCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string oldpath, 
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string newpath);
	delegate int ChangePathPermissionsCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, uint mode);
	delegate int ChangePathOwnerCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, long owner, long group);
	delegate int TruncateFileb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, long length);
	delegate int ChangePathTimesCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, IntPtr buf);
	delegate int OpenHandleCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, IntPtr info); 
	delegate int ReadHandleCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, 
			[Out, MarshalAs(UnmanagedType.LPArray, ArraySubType=UnmanagedType.U1, SizeParamIndex=2)]
			byte[] buf, ulong size, long offset, IntPtr info, out int bytesRead);
	delegate int WriteHandleCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, 
			[In, MarshalAs(UnmanagedType.LPArray, ArraySubType=UnmanagedType.U1, SizeParamIndex=2)]
			byte[] buf, ulong size, long offset, IntPtr info, out int bytesWritten);
	delegate int GetFileSystemStatusCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, IntPtr buf);
	delegate int FlushHandleCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, IntPtr info);
	delegate int ReleaseHandleCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, IntPtr info);
	delegate int SynchronizeHandleCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, bool onlyUserData, IntPtr info);
	delegate int SetPathExtendedAttributeCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, 
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string name, 
			[In, MarshalAs(UnmanagedType.LPArray, ArraySubType=UnmanagedType.U1, SizeParamIndex=3)]
			byte[] value, ulong size, int flags);
	delegate int GetPathExtendedAttributeCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, 
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string name, 
			[Out, MarshalAs(UnmanagedType.LPArray, ArraySubType=UnmanagedType.U1, SizeParamIndex=3)]
			byte[] value, ulong size, out int bytesWritten);
	delegate int ListPathExtendedAttributesCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, 
			[Out, MarshalAs(UnmanagedType.LPArray, ArraySubType=UnmanagedType.U1, SizeParamIndex=2)]
			byte[] list, ulong size, out int bytesWritten);
	delegate int RemovePathExtendedAttributeCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, 
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string name);
	delegate int OpenDirectoryCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, IntPtr info);
	delegate int ReadDirectoryCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, IntPtr buf, IntPtr filler, 
			long offset, IntPtr info, IntPtr stbuf);
	delegate int ReleaseDirectoryCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, IntPtr info);
	delegate int SynchronizeDirectoryCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, bool onlyUserData, IntPtr info);
	delegate IntPtr InitCb(IntPtr conn);
	delegate void DestroyCb(IntPtr conn);
	delegate int AccessPathCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, int mode);
	delegate int CreateHandleCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, uint mode, IntPtr info);
	delegate int TruncateHandleCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, long length, IntPtr info);
	delegate int GetHandleStatusCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, IntPtr buf, IntPtr info);
	delegate int LockHandleCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, IntPtr info, int cmd, IntPtr flockp);
	// TODO: utimens
	delegate int MapPathLogicalToPhysicalIndexCb(
			[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
			string path, ulong logical, out ulong physical);

	[Map]
	[StructLayout(LayoutKind.Sequential)]
	class Operations
    {
		public GetPathStatusCb                getattr;
		public ReadSymbolicLinkCb             readlink;
		public CreateSpecialFileCb            mknod;
		public CreateDirectoryCb              mkdir;
		public RemoveFileCb                   unlink;
		public RemoveDirectoryCb              rmdir;
		public CreateSymbolicLinkCb           symlink;
		public RenamePathCb                   rename;
		public CreateHardLinkCb               link;
		public ChangePathPermissionsCb        chmod;
		public ChangePathOwnerCb              chown;
		public TruncateFileb                  truncate;
		public ChangePathTimesCb              utime;
		public OpenHandleCb                   open;
		public ReadHandleCb                   read;
		public WriteHandleCb                  write;
		public GetFileSystemStatusCb          statfs;
		public FlushHandleCb                  flush;
		public ReleaseHandleCb                release;
		public SynchronizeHandleCb            fsync;
		public SetPathExtendedAttributeCb     setxattr;
		public GetPathExtendedAttributeCb     getxattr;
		public ListPathExtendedAttributesCb   listxattr;
		public RemovePathExtendedAttributeCb  removexattr;
		public OpenDirectoryCb                opendir;
		public ReadDirectoryCb                readdir;
		public ReleaseDirectoryCb             releasedir;
		public SynchronizeDirectoryCb         fsyncdir;
		public InitCb                         init;
		public DestroyCb                      destroy;
		public AccessPathCb                   access;
		public CreateHandleCb                 create;
		public TruncateHandleCb               ftruncate;
		public GetHandleStatusCb              fgetattr;
		public LockHandleCb                   @lock;
		public MapPathLogicalToPhysicalIndexCb  bmap;
	}

	[Map("struct fuse_args")]
	[StructLayout(LayoutKind.Sequential)]
	class Args
    {
		public int argc;
		public IntPtr argv;
		public int allocated;
	}

	public class ConnectionInformation
    {
		readonly IntPtr _conn;

		// fuse_conn_info member offsets
		const int 
			ProtMajor = 0,
			ProtMinor = 1,
			AsyncRead = 2,
			MaxWrite  = 3,
			MaxRead   = 4;

		internal ConnectionInformation(IntPtr conn) => this._conn = conn;

		public uint ProtocolMajorVersion => (uint)Marshal.ReadInt32(_conn, ProtMajor);
		public uint ProtocolMinorVersion => (uint)Marshal.ReadInt32(_conn, ProtMinor);

		public bool AsynchronousReadSupported
        {
			get { return Marshal.ReadInt32(_conn, AsyncRead) != 0; }
			set { Marshal.WriteInt32(_conn, AsyncRead, value ? 1 : 0); }
		}

		public uint MaxWriteBufferSize
        {
			get { return (uint) Marshal.ReadInt32(_conn, MaxWrite); }
			set { Marshal.WriteInt32(_conn, MaxWrite, (int) value); }
		}

		public uint MaxReadahead
        {
			get { return (uint) Marshal.ReadInt32(_conn, MaxRead); }
			set { Marshal.WriteInt32(_conn, MaxRead, (int) value); }
		}
	}

	public abstract class FileSystem : IDisposable
    {
		const string LIB = "MonoFuseHelper";

		[DllImport(LIB, SetLastError=true)]
		static extern int mfh_fuse_main(int argc, IntPtr argv, IntPtr op);

		[DllImport(LIB, SetLastError=true)]
		static extern int mfh_fuse_get_context([In, Out] FileSystemOperationContext context);

		[DllImport(LIB, SetLastError=true)]
		static extern void mfh_fuse_exit(IntPtr fusep);

		[DllImport(LIB, SetLastError=false)]
		static extern int mfh_invoke_filler(IntPtr filler, IntPtr buf,
				[MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef=typeof(FileNameMarshaler))]
				string path, IntPtr stbuf, long offset);

		[DllImport(LIB, SetLastError=false)]
		static extern void mfh_show_fuse_help(string appname);

        readonly Dictionary<string, string> _opts = new Dictionary<string, string>();
		Operations _ops;
		IntPtr _opsp;

		protected FileSystem(string mountPoint) => MountPoint = mountPoint;

		protected FileSystem()
		{ }

        protected Errno GetResult(int result)
        {
            if (result == -1)
                return Stdlib.GetLastError();
            return 0;
        }

        protected FileSystem(string[] args)
		{
			string[] unhandled = ParseFuseArguments(args);
			MountPoint = unhandled[unhandled.Length - 1];
		}

        public IDictionary<string, string> FuseOptions => _opts;

        public bool EnableFuseDebugOutput
        {
			get { return GetBool("debug"); }
			set { Set("debug", value ? "" : null); }
		}

		public bool AllowAccessToOthers
        {
			get { return GetBool("allow_other"); }
			set { Set("allow_other", value ? "" : null); }
		}

		public bool AllowAccessToRoot
        {
			get { return GetBool("allow_root"); }
			set { Set("allow_root", value ? "" : null); }
		}

		public bool AllowMountOverNonEmptyDirectory
        {
			get { return GetBool("nonempty"); }
			set { Set("nonempty", value ? "" : null); }
		}

		public bool EnableKernelPermissionChecking
        {
			get { return GetBool("default_permissions"); }
			set { Set("default_permissions", value ? "" : null); }
		}

		public string Name
        {
			get { return GetString("fsname"); }
			set { Set("fsname", value); }
		}

		public bool EnableLargeReadRequests
        {
			get { return GetBool("large_read"); }
			set { Set("large_read", value ? "" : null); }
		}

		public int MaxReadSize
        {
			get { return (int)GetLong("max_read"); }
			set { Set("max_read", value.ToString ()); }
		}

		public bool ImmediatePathRemoval
        {
			get { return GetBool("hard_remove"); }
			set { Set("hard_remove", value ? "" : null); }
		}

		public bool SetsInodes
        {
			get { return GetBool("use_ino"); }
			set { Set("use_ino", value ? "" : null); }
		}

		public bool ReaddirSetsInode
        {
			get { return GetBool("readdir_ino"); }
			set { Set("readdir_ino", value ? "" : null); }
		}

		public bool EnableDirectIO
        {
			get { return GetBool("direct_io"); }
			set { Set("direct_io", value ? "" : null); }
		}

		public bool EnableKernelCache
        {
			get { return GetBool("kernel_cache"); }
			set { Set("kernel_cache", value ? "" : null); }
		}

		public FilePermissions DefaultUmask
        {
			get
            {
				string umask = GetString("umask") ?? "0000";
				return NativeConvert.FromOctalPermissionString(umask);
			}
			set
            {
				Set("umask", NativeConvert.ToOctalPermissionString(value));
			}
		}

		public long DefaultUserId
        {
			get { return GetLong("uid"); }
			set { Set("uid", value.ToString ()); }
		}

		public long DefaultGroupId
        {
			get { return GetLong("gid"); }
			set { Set("gid", value.ToString ()); }
		}

		public double PathTimeout
        {
			get { return (int)GetDouble("entry_timeout"); }
			set { Set("entry_timeout", value.ToString()); }
		}

		public double DeletedPathTimeout
        {
			get { return (int)GetDouble("negative_timeout"); }
			set { Set("negative_timeout", value.ToString()); }
		}

		public double AttributeTimeout
        {
			get { return (int)GetDouble("attr_timeout"); }
			set { Set("attr_timeout", value.ToString()); }
		}

		bool GetBool(string key) => _opts.ContainsKey(key);

        double GetDouble(string key)
		{
			if (_opts.ContainsKey(key))
				return double.Parse(_opts[key]);
			return 0.0;
		}

		string GetString(string key)
		{
			if (_opts.ContainsKey(key))
				return _opts[key];
			return "";
		}

		long GetLong(string key)
		{
			if (_opts.ContainsKey(key))
				return long.Parse(_opts[key]);
			return 0;
		}

		void Set(string key, string value)
		{
			if (value == null)
            {
				_opts.Remove(key);
				return;
			}
			_opts[key] = value;
		}

        public string MountPoint { get; set; }
        public bool MultiThreaded { get; set; } = true;

        const string NameValueRegex = @"(?<Name>\w+)(\s*=\s*(?<Value>.*))?";
		const string OptRegex = @"^-o\s*(" + NameValueRegex + ")?$";

		public string[] ParseFuseArguments(string[] args)
		{
			var unhandled = new List<string>();
			var o = new Regex(OptRegex);
			var nv = new Regex(NameValueRegex);
			var interpret = true;

			for (int i = 0; i < args.Length; ++i)
            {
				if (!interpret)
                {
					unhandled.Add(args[i]);
					continue;
				}
				Match m = o.Match(args[i]);
				if (m.Success)
                {
					if (!m.Groups["Name"].Success)
                    {
						m = nv.Match(args[++i]);
						if (!m.Success)
							throw new ArgumentException("args");
					}
					_opts[m.Groups["Name"].Value] = 
						m.Groups["Value"].Success ? m.Groups["Value"].Value : "";
				}
				else if (args[i] == "-d") _opts ["debug"] = "";
				else if (args[i] == "-s") MultiThreaded = false;
				else if (args[i] == "-f") {
					// foreground operation; ignore 
					// (we can only do foreground operation anyway)
				}
				else if (args[i] == "--") interpret = false;
				else unhandled.Add(args[i]);
			}
			return unhandled.ToArray();
		}

		public static void ShowFuseHelp(string appname) => mfh_show_fuse_help(appname);

        string[] GetFuseArgs()
		{
			var args = new string[_opts.Keys.Count + 3 + (!MultiThreaded ? 1 : 0)];
			int i = 0;
			args[i++] = Environment.GetCommandLineArgs()[0];
			foreach (string key in _opts.Keys)
            {
				if (key == "debug")
                {
					args[i++] = "-d";
					continue;
				}
				string v = _opts[key];
				string a = "-o" + key;
				if (v.Length > 0)
					a += "=" + v.ToString ();
				args[i++] = a;
			}
			args[i++] = "-f";    // force foreground operation
			if (!MultiThreaded)
				args[i++] = "-s";  // disable multi-threaded operation
			args[i++] = MountPoint;
			return args;
		}

		static IntPtr AllocArgv(string[] args)
		{
			IntPtr argv = UnixMarshal.AllocHeap((args.Length+1) * IntPtr.Size);

			for (int i = 0; i < args.Length; ++i)
				Marshal.WriteIntPtr (argv, i*IntPtr.Size, UnixMarshal.StringToHeap(args[i]));

            Marshal.WriteIntPtr(argv, args.Length*IntPtr.Size, IntPtr.Zero);
			return argv;
		}

		static void FreeArgv (int argc, IntPtr argv)
		{
			if (argv == IntPtr.Zero)
				return;
			for (int i = 0; i < argc; ++i)
            {
				IntPtr p = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
				UnixMarshal.FreeHeap(p);
			}
			UnixMarshal.FreeHeap(argv);
		}

		delegate void CopyOperation (Operations to, FileSystem from);
		static readonly Dictionary <string, CopyOperation> operations;

		static FileSystem ()
		{
			operations = new Dictionary <string, CopyOperation> {
				{ "OnGetPathStatus",               (to, from) => { to.getattr     = from._OnGetPathStatus; } },
				{ "OnReadSymbolicLink",            (to, from) => { to.readlink    = from._OnReadSymbolicLink; } },
				{ "OnCreateSpecialFile",           (to, from) => { to.mknod       = from._OnCreateSpecialFile; } },
				{ "OnCreateDirectory",             (to, from) => { to.mkdir       = from._OnCreateDirectory; } },
				{ "OnRemoveFile",                  (to, from) => { to.unlink      = from._OnRemoveFile; } },
				{ "OnRemoveDirectory",             (to, from) => { to.rmdir       = from._OnRemoveDirectory; } },
				{ "OnCreateSymbolicLink",          (to, from) => { to.symlink     = from._OnCreateSymbolicLink; } },
				{ "OnRenamePath",                  (to, from) => { to.rename      = from._OnRenamePath; } },
				{ "OnCreateHardLink",              (to, from) => { to.link        = from._OnCreateHardLink; } },
				{ "OnChangePathPermissions",       (to, from) => { to.chmod       = from._OnChangePathPermissions; } },
				{ "OnChangePathOwner",             (to, from) => { to.chown       = from._OnChangePathOwner; } },
				{ "OnTruncateFile",                (to, from) => { to.truncate    = from._OnTruncateFile; } },
				{ "OnChangePathTimes",             (to, from) => { to.utime       = from._OnChangePathTimes; } },
				{ "OnOpenHandle",                  (to, from) => { to.open        = from._OnOpenHandle; } },
				{ "OnReadHandle",                  (to, from) => { to.read        = from._OnReadHandle; } },
				{ "OnWriteHandle",                 (to, from) => { to.write       = from._OnWriteHandle; } },
				{ "OnGetFileSystemStatus",         (to, from) => { to.statfs      = from._OnGetFileSystemStatus; } },
				{ "OnFlushHandle",                 (to, from) => { to.flush       = from._OnFlushHandle; } },
				{ "OnReleaseHandle",               (to, from) => { to.release     = from._OnReleaseHandle; } },
				{ "OnSynchronizeHandle",           (to, from) => { to.fsync       = from._OnSynchronizeHandle; } },
				{ "OnSetPathExtendedAttribute",    (to, from) => { to.setxattr    = from._OnSetPathExtendedAttribute; } },
				{ "OnGetPathExtendedAttribute",    (to, from) => { to.getxattr    = from._OnGetPathExtendedAttribute; } },
				{ "OnListPathExtendedAttributes",  (to, from) => { to.listxattr   = from._OnListPathExtendedAttributes; } },
				{ "OnRemovePathExtendedAttribute", (to, from) => { to.removexattr = from._OnRemovePathExtendedAttribute; } },
				{ "OnOpenDirectory",               (to, from) => { to.opendir     = from._OnOpenDirectory; } },
				{ "OnReadDirectory",               (to, from) => { to.readdir     = from._OnReadDirectory; } },
				{ "OnReleaseDirectory",            (to, from) => { to.releasedir  = from._OnReleaseDirectory; } },
				{ "OnSynchronizeDirectory",        (to, from) => { to.fsyncdir    = from._OnSynchronizeDirectory; } },
				{ "OnAccessPath",                  (to, from) => { to.access      = from._OnAccessPath; } },
				{ "OnCreateHandle",                (to, from) => { to.create      = from._OnCreateHandle; } },
				{ "OnTruncateHandle",              (to, from) => { to.ftruncate   = from._OnTruncateHandle; } },
				{ "OnGetHandleStatus",             (to, from) => { to.fgetattr    = from._OnGetHandleStatus; } },
				{ "OnLockHandle",                  (to, from) => { to.@lock       = from._OnLockHandle; } },
				{ "OnMapPathLogicalToPhysicalIndex", (to, from) => { to.bmap      = from._OnMapPathLogicalToPhysicalIndex; } },
			};
		}

 		Operations GetOperations()
 		{
            var ops = new Operations
            {
                init = _OnInit,
                destroy = _OnDestroy
            };
            foreach (string method in operations.Keys)
            {
				MethodInfo m = this.GetType().GetMethod(method, 
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
				MethodInfo bm = m.GetBaseDefinition();
				if (m.DeclaringType == typeof(FileSystem) || bm == null || bm.DeclaringType != typeof(FileSystem))
					continue;
				CopyOperation op = operations[method];
				op(ops, this);
			}

			ValidateOperations(ops);

 			return ops;
 		}
 
		static void ValidateOperations(Operations ops)
		{
			// some methods need to be overridden in sets for sane operation
			if (ops.opendir != null && ops.releasedir == null)
				throw new InvalidOperationException("OnReleaseDirectory() must be overridden if OnOpenDirectory() is overridden.");
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
				_ops = null;
		}

		~FileSystem() => Dispose(false);

        public void Start()
		{
			if (MountPoint == null)
				throw new InvalidOperationException("MountPoint must not be null");
			string[] args = GetFuseArgs();
			IntPtr argv = AllocArgv(args);
			try
            {
				_ops = GetOperations();
				_opsp = UnixMarshal.AllocHeap(Marshal.SizeOf(_ops));
				Marshal.StructureToPtr(_ops, _opsp, false);

                int r = mfh_fuse_main(args.Length, argv, _opsp);
				if (r != 0)
					throw new NotSupportedException($"Unable to mount directory `{MountPoint}'; try running `/sbin/modprobe fuse' as the root user");
			}
			finally
            {
				FreeArgv(args.Length, argv);
			}
		}

		public void Stop() => mfh_fuse_exit(GetOperationContext().fuse);

        protected static FileSystemOperationContext GetOperationContext()
		{
			var context = new FileSystemOperationContext();
			int r = mfh_fuse_get_context(context);
			UnixMarshal.ThrowExceptionForLastErrorIf(r);
			return context;
		}

		[DllImport(LIB, SetLastError=true)]
		static extern int Mono_Fuse_NETStandard_FromOpenedPathInfo(OpenedPathInfo source, IntPtr dest);

		[DllImport(LIB, SetLastError=true)]
		static extern int Mono_Fuse_NETStandard_ToOpenedPathInfo(IntPtr source, [Out] OpenedPathInfo dest);

		static void CopyFlock(IntPtr source, out Flock dest)
		{
			if (!NativeConvert.TryCopy(source, out dest))
				throw new ArgumentOutOfRangeException("Unable to copy `struct flock' into Mono.Unix.Native.Flock.");
		}

		 static void CopyFlock(ref Flock source, IntPtr dest)
		{
			if (!NativeConvert.TryCopy(ref source, dest))
				throw new ArgumentOutOfRangeException("Unable to copy Mono.Unix.Native.Flock into `struct flock'.");
		}

		static void CopyStat(IntPtr source, out Stat dest)
		{
			if (!NativeConvert.TryCopy(source, out dest))
				throw new ArgumentOutOfRangeException("Unable to copy `struct stat' into Mono.Unix.Native.Stat.");
		}

		static void CopyStat(ref Stat source, IntPtr dest)
		{
			if (!NativeConvert.TryCopy(ref source, dest))
				throw new ArgumentOutOfRangeException("Unable to copy Mono.Unix.Native.Stat into `struct stat'.");
		}

		static void CopyStatvfs(IntPtr source, out Statvfs dest)
		{
			if (!NativeConvert.TryCopy(source, out dest))
				throw new ArgumentOutOfRangeException("Unable to copy `struct statvfs' into Mono.Unix.Native.Statvfs.");
		}

		static void CopyStatvfs(ref Statvfs source, IntPtr dest)
		{
			if (!NativeConvert.TryCopy(ref source, dest))
				throw new ArgumentOutOfRangeException("Unable to copy Mono.Unix.Native.Statvfs into `struct statvfs'.");
		}

		static void CopyUtimbuf(IntPtr source, out Utimbuf dest)
		{
			if (!NativeConvert.TryCopy(source, out dest))
				throw new ArgumentOutOfRangeException("Unable to copy `struct utimbuf' into Mono.Unix.Native.Utimbuf.");
		}

		static void CopyUtimbuf(ref Utimbuf source, IntPtr dest)
		{
			if (!NativeConvert.TryCopy(ref source, dest))
				throw new ArgumentOutOfRangeException("Unable to copy Mono.Unix.Native.Utimbuf into `struct utimbuf'.");
		}

		static void CopyOpenedPathInfo(IntPtr source, OpenedPathInfo dest)
		{
			Mono_Fuse_NETStandard_ToOpenedPathInfo(source, dest);
			dest.flags = NativeConvert.ToOpenFlags((int) dest.flags);
		}

		static void CopyOpenedPathInfo(OpenedPathInfo source, IntPtr dest)
		{
			source.flags = (OpenFlags)NativeConvert.FromOpenFlags(source.flags);
			Mono_Fuse_NETStandard_FromOpenedPathInfo(source, dest);
		}

		int _OnGetPathStatus(string path, IntPtr stat)
		{
			Errno errno;
			try
            {
                CopyStat(stat, out Stat buf);
                errno = OnGetPathStatus(path, out buf);
				if (errno == 0)
					CopyStat(ref buf, stat);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		int ConvertErrno(Errno e)
		{
            if (NativeConvert.TryFromErrno(e, out int r))
                return -r;
            return -1;
		}

		protected virtual Errno OnGetPathStatus(string path, out Stat stat)
		{
			stat = new Stat();
			return Errno.ENOSYS;
		}

		int _OnReadSymbolicLink(string path, IntPtr buf, ulong bufsize)
		{
			Errno errno;
			try
            {
				if (bufsize <= 1)
					return ConvertErrno(Errno.EINVAL);
                errno = OnReadSymbolicLink(path, out string target);
                if (errno == 0 && target != null)
                {
					byte[] b = _encoding.GetBytes(target);
					if ((bufsize-1) < (ulong) b.Length)
						errno = Errno.EINVAL;
					else
                    {
						Marshal.Copy(b, 0, buf, b.Length);
						Marshal.WriteByte(buf, b.Length, 0);
					}
				}
				else if (errno == 0 && target == null)
                {
					Trace.WriteLine("OnReadSymbolicLink: error: 0 return value but target is `null'");
					errno = Errno.EIO;
				}
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		readonly static Encoding _encoding = new UTF8Encoding(false, true);

		protected virtual Errno OnReadSymbolicLink(string link, out string target)
		{
			target = null;
			return Errno.ENOSYS;
		}

		int _OnCreateSpecialFile(string path, uint perms, ulong dev)
		{
			Errno errno;
			try
            {
				var _perms = NativeConvert.ToFilePermissions(perms);
				errno = OnCreateSpecialFile(path, _perms, dev);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnCreateSpecialFile(string file, FilePermissions perms, ulong dev)
		    => Errno.ENOSYS;

        int _OnCreateDirectory(string path, uint mode)
		{
			Errno errno;
			try
            {
				var _mode = NativeConvert.ToFilePermissions(mode);
				errno = OnCreateDirectory(path, _mode);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnCreateDirectory(string directory, FilePermissions mode)
		    => Errno.ENOSYS;

        int _OnRemoveFile(string path)
		{
			Errno errno;
			try
            {
				errno = OnRemoveFile(path);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnRemoveFile(string file)
		    => Errno.ENOSYS;

        int _OnRemoveDirectory(string path)
		{
			Errno errno;
			try
            {
				errno = OnRemoveDirectory(path);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnRemoveDirectory(string directory)
		    => Errno.ENOSYS;

        int _OnCreateSymbolicLink(string oldpath, string newpath)
		{
			Errno errno;
			try
            {
				errno = OnCreateSymbolicLink(oldpath, newpath);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnCreateSymbolicLink(string target, string link)
		    => Errno.ENOSYS;

        int _OnRenamePath(string oldpath, string newpath)
		{
			Errno errno;
			try
            {
				errno = OnRenamePath(oldpath, newpath);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnRenamePath(string oldpath, string newpath)
		    => Errno.ENOSYS;

        int _OnCreateHardLink(string oldpath, string newpath)
		{
			Errno errno;
			try
            {
				errno = OnCreateHardLink(oldpath, newpath);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnCreateHardLink(string oldpath, string link)
		    => Errno.ENOSYS;

        int _OnChangePathPermissions(string path, uint mode)
		{
			Errno errno;
			try
            {
				var _mode = NativeConvert.ToFilePermissions(mode);
				errno = OnChangePathPermissions(path, _mode);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnChangePathPermissions(string path, FilePermissions mode)
		    => Errno.ENOSYS;

        int _OnChangePathOwner(string path, long owner, long group)
		{
			Errno errno;
			try
            {
				errno = OnChangePathOwner(path, owner, group);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnChangePathOwner(string path, long owner, long group)
		    => Errno.ENOSYS;

        int _OnTruncateFile(string path, long length)
		{
			Errno errno;
			try
            {
				errno = OnTruncateFile(path, length);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnTruncateFile(string file, long length)
		    => Errno.ENOSYS;

        // TODO: can buf be null?
        int _OnChangePathTimes(string path, IntPtr buf)
		{
			Errno errno;
			try
            {
                CopyUtimbuf(buf, out Utimbuf b);
                errno = OnChangePathTimes(path, ref b);
				if (errno == 0)
					CopyUtimbuf(ref b, buf);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnChangePathTimes(string path, ref Utimbuf buf)
		    => Errno.ENOSYS;

        int _OnOpenHandle(string path, IntPtr fi)
		{
			Errno errno;
			try
            {
				var info = new OpenedPathInfo();
				CopyOpenedPathInfo(fi, info);
				errno = OnOpenHandle(path, info);
				if (errno == 0)
					CopyOpenedPathInfo(info, fi);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnOpenHandle(string file, OpenedPathInfo info)
		    => Errno.ENOSYS;

        int _OnReadHandle(string path, byte[] buf, ulong size, long offset, IntPtr fi, out int bytesWritten)
		{
			Errno errno;
			try
            {
				var info = new OpenedPathInfo();
				CopyOpenedPathInfo(fi, info);
				errno = OnReadHandle(path, info, buf, offset, out bytesWritten);
				if (errno == 0)
					CopyOpenedPathInfo(info, fi);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				bytesWritten = 0;
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnReadHandle(string file, OpenedPathInfo info, byte[] buf, long offset, out int bytesWritten)
		{
			bytesWritten = 0;
			return Errno.ENOSYS;
		}

		int _OnWriteHandle (string path, byte[] buf, ulong size, long offset, IntPtr fi, out int bytesRead)
		{
			Errno errno;
			try
            {
				var info = new OpenedPathInfo();
				CopyOpenedPathInfo(fi, info);
				errno = OnWriteHandle(path, info, buf, offset, out bytesRead);
				if (errno == 0)
					CopyOpenedPathInfo(info, fi);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				bytesRead = 0;
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnWriteHandle(string file, OpenedPathInfo info, byte[] buf, long offset, out int bytesRead)
		{
			bytesRead = 0;
			return Errno.ENOSYS;
		}

		int _OnGetFileSystemStatus(string path, IntPtr buf)
		{
			Errno errno;
			try
            {
                CopyStatvfs(buf, out Statvfs b);
                errno = OnGetFileSystemStatus(path, out b);
				if (errno == 0)
					CopyStatvfs(ref b, buf);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnGetFileSystemStatus(string path, out Statvfs buf)
		{
			buf = new Statvfs();
			return Errno.ENOSYS;
		}

		int _OnFlushHandle(string path, IntPtr fi)
		{
			Errno errno;
			try
            {
				var info = new OpenedPathInfo();
				CopyOpenedPathInfo(fi, info);
				errno = OnFlushHandle(path, info);
				CopyOpenedPathInfo(info, fi);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnFlushHandle(string file, OpenedPathInfo info)
		    => Errno.ENOSYS;

        int _OnReleaseHandle(string path, IntPtr fi)
		{
			Errno errno;
			try
            {
				var info = new OpenedPathInfo();
				CopyOpenedPathInfo(fi, info);
				errno = OnReleaseHandle(path, info);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnReleaseHandle(string file, OpenedPathInfo info)
		    => Errno.ENOSYS;

        int _OnSynchronizeHandle(string path, bool onlyUserData, IntPtr fi)
		{
			Errno errno;
			try
            {
				var info = new OpenedPathInfo();
				CopyOpenedPathInfo(fi, info);
				errno = OnSynchronizeHandle(path, info, onlyUserData);
				if (errno == 0)
					CopyOpenedPathInfo(info, fi);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnSynchronizeHandle(string file, OpenedPathInfo info, bool onlyUserData)
		    => Errno.ENOSYS;

        int _OnSetPathExtendedAttribute(string path, string name, byte[] value, ulong size, int flags)
		{
			Errno errno;
			try
            {
				var f = NativeConvert.ToXattrFlags(flags);
				errno = OnSetPathExtendedAttribute(path, name, value, f);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnSetPathExtendedAttribute(string path, string name, byte[] value, XattrFlags flags)
		    => Errno.ENOSYS;

        int _OnGetPathExtendedAttribute(string path, string name, byte[] value, ulong size, out int bytesWritten)
		{
			Errno errno;
			try
            {
				errno = OnGetPathExtendedAttribute(path, name, value, out bytesWritten);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				bytesWritten = 0;
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnGetPathExtendedAttribute(string path, string name, byte[] value, out int bytesWritten)
		{
			bytesWritten = 0;
			return Errno.ENOSYS;
		}

		int _OnListPathExtendedAttributes(string path, byte[] list, ulong size,  out int bytesWritten)
		{
			Errno errno;
			try
            {
				bytesWritten = 0;
                errno = OnListPathExtendedAttributes(path, out string[] names);
                if (errno == 0 && names != null)
                {
					int bytesNeeded = 0;
					for (int i = 0; i < names.Length; ++i)
						bytesNeeded += _encoding.GetByteCount(names[i]) + 1;
					if (size == 0)
						bytesWritten = bytesNeeded;
					if (size < (ulong)bytesNeeded)
						errno = Errno.ERANGE;
					else
                    {
						int dest = 0;
						for (int i = 0; i < names.Length; ++i)
                        {
							int b = _encoding.GetBytes(names [i], 0, names [i].Length, list, dest);
							list[dest+b] = (byte) '\0';
							dest += b + 1;
						}
						bytesWritten = dest;
					}
				}
				else
					bytesWritten = 0;
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				bytesWritten = 0;
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnListPathExtendedAttributes(string path, out string[] names)
		{
			names = null;
			return Errno.ENOSYS;
		}

		int _OnRemovePathExtendedAttribute(string path, string name)
		{
			Errno errno;
			try
            {
				errno = OnRemovePathExtendedAttribute(path, name);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnRemovePathExtendedAttribute(string path, string name)
		    => Errno.ENOSYS;

        int _OnOpenDirectory(string path, IntPtr fi)
		{
			Errno errno;
			try
            {
				var info = new OpenedPathInfo();
				CopyOpenedPathInfo(fi, info);
				errno = OnOpenDirectory(path, info);
				if (errno == 0)
					CopyOpenedPathInfo(info, fi);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnOpenDirectory(string directory, OpenedPathInfo info)
		    => Errno.ENOSYS;

        readonly object _directoryLock = new object();

		readonly Dictionary<string, EntryEnumerator> _directoryReaders = 
			new Dictionary <string, EntryEnumerator>();

		readonly Random _directoryKeys = new Random();

		int _OnReadDirectory(string path, IntPtr buf, IntPtr filler, 
				long offset, IntPtr fi, IntPtr stbuf)
		{
			Errno errno = 0;
			try
            {
				if (offset == 0)
					GetDirectoryEnumerator(path, fi, out offset, out errno);
				if (errno != 0)
					return ConvertErrno(errno);

				EntryEnumerator entries = null;
				lock (_directoryLock)
                {
					var key = offset.ToString();
					if (_directoryReaders.ContainsKey(key))
						entries = _directoryReaders[key];
				}

				// FUSE will invoke _OnReadDirectory at least twice, but if there were
				// very few entries then the enumerator will get cleaned up during the
				// first call, so this is (1) expected, and (2) ignorable.
				if (entries == null)
					return 0;

				bool cleanup = FillEntries(filler, buf, stbuf, offset, entries);

				if (cleanup)
                {
					entries.Dispose();
					lock (_directoryLock)
                    {
						_directoryReaders.Remove(offset.ToString());
					}
				}
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno (errno);
		}

		void GetDirectoryEnumerator (string path, IntPtr fi, out long offset, out Errno errno)
		{
			var info = new OpenedPathInfo();
			CopyOpenedPathInfo(fi, info);

			offset = -1;

            errno = OnReadDirectory(path, info, out IEnumerable<DirectoryEntry> paths);
            if (errno != 0)
				return;
			if (paths == null)
            {
				Trace.WriteLine("OnReadDirectory: errno = 0 but paths is null!");
				errno = Errno.EIO;
				return;
			}
			IEnumerator<DirectoryEntry> e = paths.GetEnumerator();
			if (e == null)
            {
				Trace.WriteLine("OnReadDirectory: errno = 0 but enumerator is null!");
				errno = Errno.EIO;
				return;
			}
			int key;
			lock (_directoryLock)
            {
				do
                {
					key = _directoryKeys.Next(1, int.MaxValue);
				} while (_directoryReaders.ContainsKey(key.ToString()));

				_directoryReaders[key.ToString()] = new EntryEnumerator(e);
			}

			CopyOpenedPathInfo(info, fi);

			offset = key;
			errno  = 0;
		}

		class EntryEnumerator : IEnumerator<DirectoryEntry>
        {
			readonly IEnumerator<DirectoryEntry> _entries;
			bool _repeat;

			public EntryEnumerator (IEnumerator<DirectoryEntry> entries)
			    => _entries = entries;

            public DirectoryEntry Current => _entries.Current;

            object IEnumerator.Current => Current;

            public bool Repeat
            {
				set => _repeat = value;
			}

			public bool MoveNext()
			{
				if (_repeat)
                {
					_repeat = false;
					return true;
				}
				return _entries.MoveNext();
			}

			public void Reset() => throw new InvalidOperationException();
			public void Dispose() => _entries.Dispose();
        }

		bool FillEntries(IntPtr filler, IntPtr buf, IntPtr stbuf, 
				long offset, EntryEnumerator entries)
		{
			while (entries.MoveNext())
            {
				DirectoryEntry entry = entries.Current;
				IntPtr _stbuf = IntPtr.Zero;
				if (entry.Stat.st_ino != 0)
                {
					CopyStat(ref entry.Stat, stbuf);
					_stbuf = stbuf;
				}
				int r = mfh_invoke_filler(filler, buf, entry.Name, _stbuf, offset);
				if (r != 0)
                {
					entries.Repeat = true;
					return false;
				}
			}
			return true;
		}

		protected virtual Errno OnReadDirectory(string directory, OpenedPathInfo info, 
				out IEnumerable<DirectoryEntry> paths)
		{
			paths = null;
			return Errno.ENOSYS;
		}

		int _OnReleaseDirectory(string path, IntPtr fi)
		{
			Errno errno;
			try
            {
				var info = new OpenedPathInfo();
				CopyOpenedPathInfo(fi, info);
				errno = OnReleaseDirectory(path, info);
				if (errno == 0)
					CopyOpenedPathInfo(info, fi);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnReleaseDirectory(string directory, OpenedPathInfo info)
		    => Errno.ENOSYS;

        int _OnSynchronizeDirectory(string path, bool onlyUserData, IntPtr fi)
		{
			Errno errno;
			try
            {
				var info = new OpenedPathInfo();
				CopyOpenedPathInfo(fi, info);
				errno = OnSynchronizeDirectory(path, info, onlyUserData);
				if (errno == 0)
					CopyOpenedPathInfo(info, fi);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnSynchronizeDirectory(string directory, OpenedPathInfo info, bool onlyUserData)
		    => Errno.ENOSYS;

        IntPtr _OnInit(IntPtr conn)
		{
            try
            {
				OnInit(new ConnectionInformation (conn));
				return _opsp;
			}
			catch
            {
				return IntPtr.Zero;
			}
		}

		protected virtual void OnInit(ConnectionInformation connection)
		{ }

		void _OnDestroy(IntPtr opsp)
		{
			Debug.Assert(opsp == this._opsp);

			Marshal.DestroyStructure(opsp, typeof(Operations));
			UnixMarshal.FreeHeap(opsp);
			this._opsp = IntPtr.Zero;

			Dispose(true);
		}

		int _OnAccessPath(string path, int mode)
		{
			Errno errno;
			try
            {
				var _mode = NativeConvert.ToAccessModes(mode);
				errno = OnAccessPath(path, _mode);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnAccessPath(string path, AccessModes mode)
		    => Errno.ENOSYS;

        int _OnCreateHandle(string path, uint mode, IntPtr fi)
		{
			Errno errno;
			try
            {
				var info = new OpenedPathInfo();
				CopyOpenedPathInfo(fi, info);
				var _mode = NativeConvert.ToFilePermissions(mode);
				errno = OnCreateHandle(path, info, _mode);
				if (errno == 0)
					CopyOpenedPathInfo(info, fi);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnCreateHandle(string file, OpenedPathInfo info, FilePermissions mode)
		    => Errno.ENOSYS;

        int _OnTruncateHandle(string path, long length, IntPtr fi)
		{
			Errno errno;
			try
            {
				var info = new OpenedPathInfo();
				CopyOpenedPathInfo(fi, info);
				errno = OnTruncateHandle(path, info, length);
				if (errno == 0)
					CopyOpenedPathInfo(info, fi);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnTruncateHandle(string file, OpenedPathInfo info, long length)
		    => Errno.ENOSYS;

        int _OnGetHandleStatus(string path, IntPtr buf, IntPtr fi)
		{
			Errno errno;
			try
            {
				var info = new OpenedPathInfo();
				CopyOpenedPathInfo(fi, info);
                CopyStat(buf, out Stat b);
                errno = OnGetHandleStatus(path, info, out b);
				if (errno == 0)
                {
					CopyStat(ref b, buf);
					CopyOpenedPathInfo(info, fi);
				}
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnGetHandleStatus(string file, OpenedPathInfo info, out Stat buf)
		{
			buf = new Stat();
			return Errno.ENOSYS;
		}

		int _OnLockHandle(string file, IntPtr fi, int cmd, IntPtr lockp)
		{
			Errno errno;
			try
            {
				var info = new OpenedPathInfo();
				CopyOpenedPathInfo(fi, info);
				var _cmd = NativeConvert.ToFcntlCommand(cmd);
                CopyFlock(lockp, out Flock @lock);
                errno = OnLockHandle(file, info, _cmd, ref @lock);
				if (errno == 0)
                {
					CopyFlock(ref @lock, lockp);
					CopyOpenedPathInfo(info, fi);
				}
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnLockHandle(string file, OpenedPathInfo info, FcntlCommand cmd, ref Flock @lock)
		    => Errno.ENOSYS;

        int _OnMapPathLogicalToPhysicalIndex(string path, ulong logical, out ulong physical)
		{
			Errno errno;
			try
            {
				errno = OnMapPathLogicalToPhysicalIndex(path, logical, out physical);
			}
			catch (Exception e)
            {
				Trace.WriteLine(e.ToString());
				physical = ulong.MaxValue;
				errno = Errno.EIO;
			}
			return ConvertErrno(errno);
		}

		protected virtual Errno OnMapPathLogicalToPhysicalIndex(string path, ulong logical, out ulong physical)
		{
			physical = ulong.MaxValue;
			return Errno.ENOSYS;
		}
	}
}