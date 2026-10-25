using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace OpenStreetsLauncher {
    public static class ReleasePublisher {
        public static string Prepare(string root,string sourceUrl,string archive) {
            var match=Regex.Match(sourceUrl??"","\\Ahttps://github.com/([A-Za-z0-9-]+/[A-Za-z0-9_.-]+)/releases/latest/download/latest\\.json\\z");
            if(!match.Success)throw new IOException("Save your GitHub repository link as the update address first.");
            string version;
            using(var package=new UpdatePackage(archive))version=package.Record.Version;
            var numeric=Regex.Match(version,"\\A([0-9]+\\.[0-9]+(?:\\.[0-9]+)?)(?: |$)");
            if(!numeric.Success)throw new IOException("The game update needs a numeric version.");
            string tag="v"+numeric.Groups[1].Value,repository=match.Groups[1].Value;
            string folder=Path.Combine(root,"Publish",tag);Directory.CreateDirectory(folder);
            string zip=Path.Combine(folder,"OpenStreets-update.zip");
            if(Path.GetFullPath(archive)!=Path.GetFullPath(zip))File.Copy(archive,zip,true);
            var release=new OnlineRelease{game="Open Streets",version=version,zip_url="https://github.com/"+repository+"/releases/download/"+tag+"/OpenStreets-update.zip",sha256=GamePackage.FileHash(zip),size=new FileInfo(zip).Length,notes="New Open Streets game update."};
            File.WriteAllText(Path.Combine(folder,"latest.json"),new JavaScriptSerializer().Serialize(release),new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder,"UPLOAD-INSTRUCTIONS.txt"),"Create a GitHub release with tag "+tag+" at https://github.com/"+repository+"/releases/new\r\nAttach latest.json and OpenStreets-update.zip, then publish the release.\r\nYour friends use the same launcher > Check Updates > Download Update.\r\nOnly these two assets need uploading each time. Keep previous releases available.\r\nBefore sharing the launcher for the first time, include update-source.json beside it and the complete Game folder.\r\n",Encoding.UTF8);
            return folder;
        }
    }
}
