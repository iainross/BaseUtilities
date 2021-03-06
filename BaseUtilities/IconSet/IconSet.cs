﻿/*
 * Copyright © 2016-2020 EDDiscovery development team
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not use this
 * file except in compliance with the License. You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software distributed under
 * the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF
 * ANY KIND, either express or implied. See the License for the specific language
 * governing permissions and limitations under the License.
 * 
 * EDDiscovery is not affiliated with Frontier Developments plc.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Reflection;
using System.IO;
using System.IO.Compression;
using System.Globalization;

namespace BaseUtils.Icons
{
    public class IconSet : IIcons
    {
        public Dictionary<string, Image> Icons { get; private set; } = new Dictionary<string, Image>();

        public IconSet() { }

        public void LoadIconsFromAssembly(Assembly asm)
        {
            string[] resnames = asm.GetManifestResourceNames();
            string basename = asm.GetName().Name + ".";

            foreach (string resname in resnames)
            {
                if (resname.StartsWith(basename) && new[] { ".png", ".jpg" }.Any(e => resname.EndsWith(e, StringComparison.InvariantCultureIgnoreCase)))
                {
                    string name = resname.Substring(basename.Length, resname.Length - basename.Length - 4);
                    Image img = Image.FromStream(asm.GetManifestResourceStream(resname));
                    name = SetImageTransparency(img, name);
                    img.Tag = name;
                    Icons[name] = img;
                }
            }
        }

        private static string SetImageTransparency(Image image, string name)
        {
            int transparentcolour;

            if (image is Bitmap && name.Length >= 9 && name[name.Length - 7] == '_' && name[name.Length - 8] == '_' && int.TryParse(name.Substring(name.Length - 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out transparentcolour))
            {
                var bmp = (Bitmap)image;
                name = name.Substring(0, name.Length - 8);
                bmp.MakeTransparent(Color.FromArgb(transparentcolour));
            }

            return name;
        }

        private void LoadIconsFromDirectory(string path, string extension)
        {
            foreach (var file in Directory.EnumerateFiles(path, "*." + extension, SearchOption.AllDirectories))
            {
                string name = file.Substring(path.Length + 1).Replace('/', '.').Replace('\\', '.').Replace("." + extension, "");
                Image img = null;

                try
                {
                    img = Image.FromFile(file);
                    name = SetImageTransparency(img, name);
                    img.Tag = name;
                }
                catch
                {
                    // Ignore any bad images
                    continue;
                }

                if (!Icons.ContainsKey(name))
                    System.Diagnostics.Debug.WriteLine("Icon Pack new unknown " + name);

                Icons[name] = img;
            }
        }

        public void LoadIconsFromDirectory(string path)      // tested 1/feb/2018
        {
            if (Directory.Exists(path))
            {
                System.Diagnostics.Debug.WriteLine("Loading icons from " + path);

                LoadIconsFromDirectory(path, "png");
                LoadIconsFromDirectory(path, "jpg");
            }
        }

        public void LoadIconsFromZipFile(string path) // may except.  tested 1/feb/2018
        {
            if (File.Exists(path))
            {
                using (var zipfile = ZipFile.Open(path, ZipArchiveMode.Read))
                {
                    System.Diagnostics.Debug.WriteLine("Loading icons from zip " + path);

                    foreach (var entry in zipfile.Entries)
                    {
                        if (new[] { ".png", ".jpg" }.Any(e => entry.FullName.EndsWith(e, StringComparison.InvariantCultureIgnoreCase)))
                        {
                            string name = entry.FullName.Substring(0, entry.FullName.Length - 4).Replace('/', '.').Replace('\\', '.');
                            Image img = null;

                            try
                            {
                                using (var zipstrm = entry.Open())
                                {
                                    var memstrm = new MemoryStream(); // Image will own this
                                    zipstrm.CopyTo(memstrm);
                                    img = Image.FromStream(memstrm);
                                    name = SetImageTransparency(img, name);
                                    img.Tag = name;
                                }
                            }
                            catch
                            {
                                // Ignore any bad images
                                continue;
                            }

                            if (!Icons.ContainsKey(name))
                                System.Diagnostics.Debug.WriteLine("Icon Pack new unknown " + name);

                            Icons[name] = img;
                        }
                    }
                }
            }
        }

        // path must not be null.  Check for it directly, or in appdir/basedir.  path may be wildcard.

        public void LoadIconPack(string path, string appdir, string basedir)
        {
            if (!Path.IsPathRooted(path))      // if its not an absolute path
            {
                string testpath = Path.Combine(appdir, path);

                if (File.Exists(testpath) || Directory.Exists(testpath))
                {
                    path = testpath;
                }
                else
                {
                    path = Path.Combine(basedir, path);
                }
            }

            //System.Diagnostics.Debug.WriteLine("ICONS Path" + path);

            try
            {
                if (File.Exists(path))      // single file
                {
                    LoadIconsFromZipFile(path);
                }
                else if (Directory.Exists(path))     // if its a directory..
                {
                    LoadIconsFromDirectory(path);
                }
                else
                {
                    string dirpart = Path.GetDirectoryName(path);

                    if (Directory.Exists(dirpart))
                    {
                        // files in date order, last first, so newer ones override 
                        FileInfo[] allFiles = Directory.EnumerateFiles(dirpart, Path.GetFileName(path), SearchOption.TopDirectoryOnly).Select(f => new System.IO.FileInfo(f)).OrderBy(p => p.LastWriteTime).ToArray();

                        foreach (FileInfo f in allFiles)
                        {
                            LoadIconsFromZipFile(f.FullName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to load icons from {path}: {ex.Message}");
            }

        }

        public void AddAlias(string name, string originalname)
        {
            Icons[name] = Get(originalname);
        }

        public void Add(string name, Image i)
        {
            Icons[name] = i;
        }

        public Image Get(string name)
        {
            if (Icons == null)      // seen designer barfing over this
                return null;

            //System.Diagnostics.Debug.WriteLine("ICON " + name);

            if (Icons.ContainsKey(name))            // written this way so you can debug step it.
                return Icons[name];
            else
            {
                Image i = Icons["Default"];         // will assert if you don't give it
                return i;
            }
        }

        // Singleton support if required

        static public IconSet Instance { get { System.Diagnostics.Debug.Assert(instance != null); return instance; } }
        static private IconSet instance;

        public static void CreateSingleton()
        {
            instance = new IconSet();
        }

        public static Image GetIcon(string name)        // static access.
        {
            return Instance.Get(name);
        }
    }
}
