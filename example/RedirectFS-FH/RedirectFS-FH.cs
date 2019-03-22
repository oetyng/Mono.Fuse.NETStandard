//
// RedirectFS-FH.cs: Port of
// http://fuse.cvs.sourceforge.net/fuse/fuse/example/fusexmp_fh.c?view=log
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
using System.Text;
using Mono.Unix.Native;

namespace Mono.Fuse.NETStandard.Samples
{
	class RedirectFHFS : FileSystem
    {
		string _basedir;

		public RedirectFHFS()
		{ }

		protected override Errno OnGetPathStatus(string path, out Stat buf)
		{
			int r = Syscall.lstat(_basedir + path, out buf);
            return GetResult(r);
        }

		protected override Errno OnGetHandleStatus(string path, OpenedPathInfo info, out Stat buf)
		{
			int r = Syscall.fstat((int)info.Handle, out buf);
            return GetResult(r);
        }

		protected override Errno OnAccessPath(string path, AccessModes mask)
		{
			int r = Syscall.access(_basedir + path, mask);
            return GetResult(r);
        }

		protected override Errno OnReadSymbolicLink(string path, out string target)
		{
			target = null;
			var buf = new StringBuilder(256);
			do
            {
				int r = Syscall.readlink(_basedir + path, buf);
				if (r < 0)
					return Stdlib.GetLastError();
				else if (r == buf.Capacity)
					buf.Capacity *= 2;
				else
                {
					target = buf.ToString(0, r);
					return 0;
				}
			} while (true);
		}

		protected override Errno OnOpenDirectory(string path, OpenedPathInfo info)
		{
			IntPtr dp = Syscall.opendir(_basedir + path);
			if (dp == IntPtr.Zero)
				return Stdlib.GetLastError();

			info.Handle = dp;
			return 0;
		}

		protected override Errno OnReadDirectory(string path, OpenedPathInfo fi,
				out IEnumerable<DirectoryEntry> paths)
		{
			var dp = fi.Handle;
			paths = ReadDirectory(dp);
			return 0;
		}

		private IEnumerable<DirectoryEntry> ReadDirectory(IntPtr dp)
		{
			Dirent de;
			while ((de = Syscall.readdir(dp)) != null)
            {
				var entry = new DirectoryEntry(de.d_name);
				entry.Stat.st_ino  = de.d_ino;
				entry.Stat.st_mode = (FilePermissions)(de.d_type << 12);
				yield return entry;
			}
		}

		protected override Errno OnReleaseDirectory(string path, OpenedPathInfo info)
		{
			var dp = info.Handle;
			Syscall.closedir(dp);
			return 0;
		}

		protected override Errno OnCreateSpecialFile(string path, FilePermissions mode, ulong rdev)
		{
			int r;

			// On Linux, this could just be `mknod(basedir+path, mode, rdev)' but 
			// this is more portable.
			if ((mode & FilePermissions.S_IFMT) == FilePermissions.S_IFREG)
            {
				r = Syscall.open(_basedir + path, OpenFlags.O_CREAT | OpenFlags.O_EXCL |
						OpenFlags.O_WRONLY, mode);
				if (r >= 0)
					r = Syscall.close(r);
			}
			else if ((mode & FilePermissions.S_IFMT) == FilePermissions.S_IFIFO)
				r = Syscall.mkfifo(_basedir + path, mode);
			else
				r = Syscall.mknod(_basedir + path, mode, rdev);

            return GetResult(r);
        }

		protected override Errno OnCreateDirectory(string path, FilePermissions mode)
		{
			int r = Syscall.mkdir(_basedir + path, mode);
            return GetResult(r);
        }

		protected override Errno OnRemoveFile(string path)
		{
			int r = Syscall.unlink(_basedir + path);
            return GetResult(r);
        }

		protected override Errno OnRemoveDirectory(string path)
		{
			int r = Syscall.rmdir(_basedir + path);
            return GetResult(r);
        }

		protected override Errno OnCreateSymbolicLink(string from, string to)
		{
			int r = Syscall.symlink(from, _basedir + to);
            return GetResult(r);
        }

		protected override Errno OnRenamePath (string from, string to)
		{
			int r = Syscall.rename(_basedir+from, _basedir + to);
            return GetResult(r);
        }

		protected override Errno OnCreateHardLink (string from, string to)
		{
			int r = Syscall.link(_basedir+from, _basedir + to);
            return GetResult(r);
        }

		protected override Errno OnChangePathPermissions(string path, FilePermissions mode)
		{
			int r = Syscall.chmod(_basedir + path, mode);
            return GetResult(r);
        }

		protected override Errno OnChangePathOwner(string path, long uid, long gid)
		{
			int r = Syscall.lchown(_basedir + path, (uint)uid, (uint)gid);
            return GetResult(r);
        }

		protected override Errno OnTruncateFile(string path, long size)
		{
			int r = Syscall.truncate(_basedir + path, size);
            return GetResult(r);
        }

		protected override Errno OnTruncateHandle(string path, OpenedPathInfo info, long size)
		{
			int r = Syscall.ftruncate((int)info.Handle, size);
            return GetResult(r);
        }

		protected override Errno OnChangePathTimes(string path, ref Utimbuf buf)
		{
			int r = Syscall.utime(_basedir + path, ref buf);
            return GetResult(r);
        }

		protected override Errno OnCreateHandle(string path, OpenedPathInfo info, FilePermissions mode)
		{
			int fd = Syscall.open(_basedir + path, info.OpenFlags, mode);
			if (fd == -1)
				return Stdlib.GetLastError();
			info.Handle = (IntPtr)fd;
			return 0;
		}

		protected override Errno OnOpenHandle(string path, OpenedPathInfo info)
		{
			int fd = Syscall.open(_basedir + path, info.OpenFlags);
			if (fd == -1)
				return Stdlib.GetLastError();
			info.Handle = (IntPtr)fd;
			return 0;
		}

		protected override unsafe Errno OnReadHandle(string path, OpenedPathInfo info, byte[] buf, 
				long offset, out int bytesRead)
		{
			int r;
			fixed (byte *pb = buf)
            {
				r = bytesRead = (int)Syscall.pread((int)info.Handle, 
						pb, (ulong)buf.Length, offset);
			}
            return GetResult(r);
        }

		protected override unsafe Errno OnWriteHandle(string path, OpenedPathInfo info,
				byte[] buf, long offset, out int bytesWritten)
		{
			int r;
			fixed (byte *pb = buf)
            {
				r = bytesWritten = (int)Syscall.pwrite((int)info.Handle, 
						pb, (ulong)buf.Length, offset);
			}
            return GetResult(r);
        }

		protected override Errno OnGetFileSystemStatus(string path, out Statvfs stbuf)
		{
			int r = Syscall.statvfs(_basedir + path, out stbuf);
            return GetResult(r);
        }

		protected override Errno OnFlushHandle(string path, OpenedPathInfo info)
		{
			/* This is called from every close on an open file, so call the
			   close on the underlying filesystem.  But since flush may be
			   called multiple times for an open file, this must not really
			   close the file.  This is important if used on a network
			   filesystem like NFS which flush the data/metadata on close() */
			int r = Syscall.close(Syscall.dup((int)info.Handle));
            return GetResult(r);
        }

		protected override Errno OnReleaseHandle(string path, OpenedPathInfo info)
		{
			int r = Syscall.close((int)info.Handle);
            return GetResult(r);
        }

		protected override Errno OnSynchronizeHandle(string path, OpenedPathInfo info, bool onlyUserData)
		{
			int r;
			if (onlyUserData)
				r = Syscall.fdatasync((int)info.Handle);
			else
				r = Syscall.fsync((int)info.Handle);
            return GetResult(r);
        }

		protected override Errno OnSetPathExtendedAttribute(string path, string name, byte[] value, XattrFlags flags)
		{
			int r = Syscall.lsetxattr(_basedir + path, name, value, (ulong)value.Length, flags);
            return GetResult(r);
        }

		protected override Errno OnGetPathExtendedAttribute(string path, string name, byte[] value, out int bytesWritten)
		{
			int r = bytesWritten = (int)Syscall.lgetxattr(_basedir + path, name, value, (ulong)value.Length);
            return GetResult(r);
        }

		protected override Errno OnListPathExtendedAttributes(string path, out string[] names)
		{
			int r = (int)Syscall.llistxattr(_basedir + path, out names);
            return GetResult(r);
        }

		protected override Errno OnRemovePathExtendedAttribute(string path, string name)
		{
			int r = Syscall.lremovexattr(_basedir + path, name);
            return GetResult(r);
        }

		protected override Errno OnLockHandle(string file, OpenedPathInfo info, FcntlCommand cmd, ref Flock @lock)
		{
			int r = Syscall.fcntl((int) info.Handle, cmd, ref @lock);
            return GetResult(r);
        }

		private bool ParseArguments(string[] args)
		{
			for (int i = 0; i < args.Length; ++i)
            {
				switch (args[i])
                {
					case "-h":
					case "--help":
						ShowHelp();
						return false;
					default:
						if (base.MountPoint == null)
							base.MountPoint = args[i];
						else
							_basedir = args[i];
						break;
				}
			}
			if (base.MountPoint == null)
				return Error("missing mountpoint");
			if(_basedir == null)
				return Error("missing basedir");
			return true;
		}

		static void ShowHelp()
		{
			Console.Error.WriteLine("usage: redirectfs [options] mountpoint:");
			FileSystem.ShowFuseHelp("redirectfs-fh");
			Console.Error.WriteLine();
			Console.Error.WriteLine("redirectfs-fh options");
			Console.Error.WriteLine("    basedir                Directory to mirror");
		}

		static bool Error(string message)
		{
			Console.Error.WriteLine("redirectfs-fh: error: {0}", message);
			return false;
		}

		public static void Main(string[] args)
		{
            using (var fs = new RedirectFHFS())
            {
                string[] unhandled = fs.ParseFuseArguments(args);
                if (!fs.ParseArguments(unhandled))
                    return;
                fs.Start();
            }
		}
	}
}