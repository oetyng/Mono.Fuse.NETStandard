//
// HelloFS.cs
//
// Authors:
//   Jonathan Pryor (jonpryor@vt.edu)
//
// (C) 2006 Jonathan Pryor
//
// Mono.Fuse.NETStandard example program
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
using System.Diagnostics;
using System.Text;
using Mono.Unix.Native;

namespace Mono.Fuse.NETStandard.Samples
{
	internal class HelloFs : FileSystem
    {
		static readonly byte[] hello_str = Encoding.UTF8.GetBytes("Hello World!\n");
		const string hello_path = "/hello";
		const string data_path  = "/data";
		const string data_im_path  = "/data.im";

		const int data_size = 100000000;

		byte[] _data_im_str;
		bool _have_data_im = false;
		readonly object _data_im_str_lock = new object();
		readonly Dictionary<string, byte[]> _hello_attrs = new Dictionary<string, byte[]>();

		public HelloFs()
		{
			Trace.WriteLine("(HelloFS creating)");
			_hello_attrs["foo"] = Encoding.UTF8.GetBytes("bar");
		}

		protected override Errno OnGetPathStatus(string path, out Stat stbuf)
		{
			Trace.WriteLine("(OnGetPathStatus {0})", path);

			stbuf = new Stat();
			switch (path) {
				case "/":
					stbuf.st_mode = FilePermissions.S_IFDIR | 
						NativeConvert.FromOctalPermissionString("0755");
					stbuf.st_nlink = 2;
					return 0;
				case hello_path:
				case data_path:
				case data_im_path:
					stbuf.st_mode = FilePermissions.S_IFREG |
						NativeConvert.FromOctalPermissionString("0444");
					stbuf.st_nlink = 1;
					int size = 0;
					switch (path)
                    {
						case hello_path:   size = hello_str.Length; break;
						case data_path:
						case data_im_path: size = data_size; break;
					}
					stbuf.st_size = size;
					return 0;
				default:
					return Errno.ENOENT;
			}
		}

		protected override Errno OnReadDirectory(string path, OpenedPathInfo fi,
				out IEnumerable<DirectoryEntry> paths)
		{
			Trace.WriteLine("(OnReadDirectory {0})", path);
			paths = null;
			if (path != "/")
				return Errno.ENOENT;

			paths = GetEntries();
			return 0;
		}

		IEnumerable<DirectoryEntry> GetEntries()
		{
			yield return new DirectoryEntry(".");
			yield return new DirectoryEntry("..");
			yield return new DirectoryEntry("hello");
			yield return new DirectoryEntry("data");
			if (_have_data_im)
				yield return new DirectoryEntry("data.im");
		}

		protected override Errno OnOpenHandle(string path, OpenedPathInfo fi)
		{
			Trace.WriteLine($"(OnOpen {path} Flags={fi.OpenFlags})");
			if (path != hello_path && path != data_path && path != data_im_path)
				return Errno.ENOENT;
			if (path == data_im_path && !_have_data_im)
				return Errno.ENOENT;
			if (fi.OpenAccess != OpenFlags.O_RDONLY)
				return Errno.EACCES;
			return 0;
		}

		protected override Errno OnReadHandle(string path, OpenedPathInfo fi, byte[] buf, long offset, out int bytesWritten)
		{
			Trace.WriteLine("(OnRead {0})", path);
			bytesWritten = 0;
			int size = buf.Length;
			if (path == data_im_path)
				FillData();
			if (path == hello_path || path == data_im_path)
            {
				byte[] source = path == hello_path ? hello_str : _data_im_str;
				if (offset < source.Length)
                {
					if (offset + size > source.Length)
						size = (int) (source.Length - offset);
					Buffer.BlockCopy(source, (int)offset, buf, 0, size);
				}
				else
					size = 0;
			}
			else if (path == data_path)
            {
				int max = Math.Min(data_size, (int)(offset + buf.Length));
				for (int i = 0, j = (int)offset; j < max; ++i, ++j)
                {
					if ((j % 27) == 0)
						buf[i] = (byte)'\n';
					else
						buf[i] = (byte)((j % 26) + 'a');
				}
			}
			else
				return Errno.ENOENT;

			bytesWritten = size;
			return 0;
		}

		protected override Errno OnGetPathExtendedAttribute(string path, string name, byte[] value, out int bytesWritten)
		{
			Trace.WriteLine("(OnGetPathExtendedAttribute {0})", path);
			bytesWritten = 0;
			if (path != hello_path)
				return 0;
			byte[] _value;
			lock (_hello_attrs)
            {
				if (!_hello_attrs.ContainsKey (name))
					return 0;
				_value = _hello_attrs [name];
			}
			if (value.Length < _value.Length)
				return Errno.ERANGE;
			Array.Copy(_value, value, _value.Length);
			bytesWritten = _value.Length;
			return 0;
		}

		protected override Errno OnSetPathExtendedAttribute(string path, string name, byte[] value, XattrFlags flags)
		{
			Trace.WriteLine("(OnSetPathExtendedAttribute {0})", path);
			if (path != hello_path)
				return Errno.ENOSPC;
			lock (_hello_attrs)
            {
				_hello_attrs[name] = value;
			}
			return 0;
		}

		protected override Errno OnRemovePathExtendedAttribute(string path, string name)
		{
			Trace.WriteLine("(OnRemovePathExtendedAttribute {0})", path);
			if (path != hello_path)
				return Errno.ENODATA;
			lock (_hello_attrs)
            {
				if (!_hello_attrs.ContainsKey (name))
					return Errno.ENODATA;
				_hello_attrs.Remove(name);
			}
			return 0;
		}

		protected override Errno OnListPathExtendedAttributes (string path, out string[] names)
		{
			Trace.WriteLine("(OnListPathExtendedAttributes {0})", path);
			if (path != hello_path)
            {
				names = new string[]{};
				return 0;
			}
			var _names = new List<string> ();
			lock (_hello_attrs)
            {
				_names.AddRange(_hello_attrs.Keys);
			}
			names = _names.ToArray ();
			return 0;
		}

		bool ParseArguments (string[] args)
		{
			for (int i = 0; i < args.Length; ++i)
            {
				switch (args [i])
                {
					case "--data.im-in-memory":
						_have_data_im = true;
						break;
					case "-h":
					case "--help":
						Console.Error.WriteLine("usage: hellofs [options] mountpoint");
						FileSystem.ShowFuseHelp ("hellofs");
						Console.Error.WriteLine("hellofs options:");
						Console.Error.WriteLine("    --data.im-in-memory    Add data.im file");
						return false;
					default:
						base.MountPoint = args [i];
						break;
				}
			}
			return true;
		}

		void FillData ()
		{
			lock (_data_im_str_lock)
            {
				if (_data_im_str != null)
					return;
				_data_im_str = new byte[data_size];
				for (int i = 0; i < _data_im_str.Length; ++i)
                {
					if ((i % 27) == 0)
						_data_im_str[i] = (byte)'\n';
					else
						_data_im_str[i] = (byte)((i % 26) + 'a');
				}
			}
		}

		public static void Main(string[] args)
		{
            using (var fs = new HelloFs())
            {
                string[] unhandled = fs.ParseFuseArguments(args);

                foreach (string key in fs.FuseOptions.Keys)
                    Console.WriteLine("Option: {0}={1}", key, fs.FuseOptions[key]);

                if (!fs.ParseArguments(unhandled))
                    return;
                // fs.MountAt("path" /* , args? */);
                fs.Start();
            }
		}
	}
}