﻿using System;
using YamlDotNet;
using YamlDotNet.Helpers;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text;

namespace DockerFileBuildHelper
{
    class Program
    {
        class Options
        {
            public string BuildAllScriptOutput { get; set; }
            public string READMEOutput { get; set; }
        }
        static int Main(string[] args)
        {
            var opts = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-o")
                    opts.BuildAllScriptOutput = args[i + 1];
                if (args[i] == "-omd")
                    opts.READMEOutput = args[i + 1];
            }
            return new Program().Run(opts) ? 0 : 1;
        }

        private bool Run(Options options)
        {
            var fragmentDirectory = Path.GetFullPath(Path.Combine(FindRoot("contrib"), "..", "docker-compose-generator", "docker-fragments"));
            List<Task<bool>> downloading = new List<Task<bool>>();
            List<DockerInfo> dockerInfos = new List<DockerInfo>();
            foreach (var image in new[]
            {
                Image.Parse("btcpayserver/docker-compose-generator"),
                Image.Parse("btcpayserver/docker-compose-builder:1.24.1"),
            }.Concat(GetImages(fragmentDirectory)))
            {
                Console.WriteLine($"Image: {image.ToString()}");
                var info = GetDockerInfo(image);
                if (info == null)
                {
                    Console.WriteLine($"Missing image info: {image}");
                    return false;
                }
                dockerInfos.Add(info);
                downloading.Add(CheckLink(info, info.DockerFilePath));
                downloading.Add(CheckLink(info, info.DockerFilePathARM32v7));
                downloading.Add(CheckLink(info, info.DockerFilePathARM64v8));
            }

            Task.WaitAll(downloading.ToArray());
            var canDownloadEverything = downloading.All(o => o.Result);
            if (!canDownloadEverything)
                return false;
            var builder = new StringBuilderEx();
            builder.AppendLine("#!/bin/bash");
            builder.AppendLine();
            builder.AppendLine("# This file is automatically generated by the DockerFileBuildHelper tool, run DockerFileBuildHelper/update-repo.sh to update it");
            builder.AppendLine("set -e");
            builder.AppendLine("DOCKERFILE=\"\"");
            builder.AppendLine();
            builder.AppendLine();
            foreach (var info in dockerInfos)
            {
                builder.AppendLine($"# Build {info.Image.Name}");
                bool mightBeUnavailable = false;
                if (info.DockerFilePath != null)
                {
                    var dockerFile = DockerFile.Parse(info.DockerFilePath);
                    builder.AppendLine($"# {info.GetGithubLinkOf(dockerFile.DockerFullPath)}");
                    builder.AppendLine($"DOCKERFILE=\"{dockerFile.DockerFullPath}\"");
                }
                else
                {
                    builder.AppendLine($"DOCKERFILE=\"\"");
                    mightBeUnavailable = true;
                }
                if (info.DockerFilePathARM32v7 != null)
                {
                    var dockerFile = DockerFile.Parse(info.DockerFilePathARM32v7);
                    builder.AppendLine($"# {info.GetGithubLinkOf(dockerFile.DockerFullPath)}");
                    builder.AppendLine($"[[ \"$(uname -m)\" == \"armv7l\" ]] && DOCKERFILE=\"{dockerFile.DockerFullPath}\"");
                }
                if (info.DockerFilePathARM64v8 != null)
                {
                    var dockerFile = DockerFile.Parse(info.DockerFilePathARM64v8);
                    builder.AppendLine($"# {info.GetGithubLinkOf(dockerFile.DockerFullPath)}");
                    builder.AppendLine($"[[ \"$(uname -m)\" == \"aarch64\" ]] && DOCKERFILE=\"{dockerFile.DockerFullPath}\"");
                }
                if (mightBeUnavailable)
                {
                    builder.AppendLine($"if [[ \"$DOCKERFILE\" ]]; then");
                    builder.Indent++;
                }
                builder.AppendLine($"echo \"Building {info.Image.ToString()}\"");
                builder.AppendLine($"git clone {info.GitLink} {info.Image.Name}");
                builder.AppendLine($"cd {info.Image.Name}");
                builder.AppendLine($"git checkout {info.GitRef}");
                builder.AppendLine($"cd \"$(dirname $DOCKERFILE)\"");
                builder.AppendLine($"docker build -f \"$DOCKERFILE\" -t \"{info.Image}\" .");
                builder.AppendLine($"cd - && cd ..");
                if (mightBeUnavailable)
                {
                    builder.Indent--;
                    builder.AppendLine($"fi");
                }
                builder.AppendLine();
                builder.AppendLine();
            }
            var script = builder.ToString().Replace("\r\n", "\n");
            if (string.IsNullOrEmpty(options.BuildAllScriptOutput))
                options.BuildAllScriptOutput = "build-all.sh";
            File.WriteAllText(options.BuildAllScriptOutput, script);
            Console.WriteLine($"Generated file \"{Path.GetFullPath(options.BuildAllScriptOutput)}\"");

            if (!string.IsNullOrEmpty(options.READMEOutput))
            {
                var readme = File.ReadAllText(options.READMEOutput);
                var start = readme.IndexOf("| Image |");
                var end = start;
                for (; end < readme.Length; end++)
                {
                    if (readme[end] == '\r' && readme[end + 1] == '\n' && readme[end + 2] != '|')
                    {
                        end += 2;
                        break;
                    }
                    if (readme[end] == '\n' && readme[end + 1] != '|')
                    {
                        end += 1;
                        break;
                    }
                }

                StringBuilder tb = new StringBuilder();
                tb.Append(readme.Substring(0, start));
                tb.AppendLine("| Image | Version | x64 | arm32v7 | arm64v8 | links |");
                tb.AppendLine("|---|---|:-:|:-:|:-:|:-:|");
				dockerInfos = dockerInfos.GroupBy(d => d.Image.ToString(false)).Select(c => c.First()).ToList();
                RenderTable(tb, dockerInfos.Where(d => d.SupportedByUs));
                RenderTable(tb, dockerInfos.Where(d => !d.SupportedByUs));
                tb.Append(readme.Substring(end));

                // RenderTable(tb, dockerInfos.Where(d => !d.SupportedByUs));
                File.WriteAllText(options.READMEOutput, tb.ToString());
            }
            return true;
        }

        void RenderTable(StringBuilder tb, IEnumerable<DockerInfo> dockerInfos)
        {
            dockerInfos = dockerInfos.OrderBy(i => i.Image.Source).ToList();
            foreach (var image in dockerInfos)
            {
                tb.Append($"| {image.Image.ToString(false)} | {image.Image.Tag} |");
                if (!string.IsNullOrEmpty(image.DockerFilePath))
                {
                    tb.Append($" [✔️]({image.GetGithubLinkOf(image.DockerFilePath)}) |");
                }
                else
                {
                    tb.Append($" ️❌ |");
                }
                if (!string.IsNullOrEmpty(image.DockerFilePathARM32v7))
                {
                    tb.Append($" [✔️]({image.GetGithubLinkOf(image.DockerFilePathARM32v7)}) |");
                }
                else
                {
                    tb.Append($" ️❌ |");
                }
                if (!string.IsNullOrEmpty(image.DockerFilePathARM64v8))
                {
                    tb.Append($" [✔️]({image.GetGithubLinkOf(image.DockerFilePathARM64v8)}) |");
                }
                else
                {
                    tb.Append($" ️❌ |");
                }
                tb.AppendLine($" [Github]({image.GitLink}) - [DockerHub]({image.DockerHubLink}) |");
            }
        }

        HttpClient client = new HttpClient();
        private async Task<bool> CheckLink(DockerInfo info, string path)
        {
            if (path == null)
                return true;
            var link = info.GetGithubLinkOf(path);
            var resp = await client.GetAsync(link);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"\tBroken link detected for image {info.Image} ({link})");
                return false;
            }
            return true;
        }

        private IEnumerable<Image> GetImages(string fragmentDirectory)
        {
            var deserializer = new DeserializerBuilder().Build();
            var serializer = new SerializerBuilder().Build();
            foreach (var file in Directory.EnumerateFiles(fragmentDirectory, "*.yml"))
            {
                var root = ParseDocument(file);
                if (root.TryGet("services") == null)
                    continue;
                foreach (var service in ((YamlMappingNode)root["services"]).Children)
                {
                    var imageStr = service.Value.TryGet("image");
                    if (imageStr == null)
                        continue;
                    var image = Image.Parse(imageStr.ToString());
                    image.Source = file;
                    yield return image;
                }
            }
        }
        private DockerInfo GetDockerInfo(Image image)
        {
            DockerInfo dockerInfo = new DockerInfo();
            var name = $"{image.User}/{image.Name}";
            bool firstTry = true;
        retry:
            switch (name)
            {
                case "pihole":
                    dockerInfo.GitLink = "https://github.com/pi-hole/docker-pi-hole";
                    dockerInfo.DockerFilePath = $"Dockerfile_amd64";
                    dockerInfo.DockerFilePathARM32v7 = $"Dockerfile_armhf";
                    dockerInfo.DockerFilePathARM64v8 = $"Dockerfile_arm64";
                    dockerInfo.GitRef = $"{image.Tag}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "eps":
                    dockerInfo.DockerFilePath = $"EPS/{NoRevision(image.Tag)}/linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"EPS/{NoRevision(image.Tag)}/linuxarm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = $"EPS/{NoRevision(image.Tag)}/linuxarm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/dockerfile-deps";
                    dockerInfo.GitRef = $"EPS/{image.Tag}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "btglnd":
                    dockerInfo.DockerFilePath = "Dockerfile";
                    dockerInfo.GitLink = "https://github.com/vutov/lnd";
                    dockerInfo.GitRef = "master";
                    break;
                case "docker-compose-builder":
                    dockerInfo.DockerFilePath = "linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = "linuxarm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = "linuxarm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/docker-compose-builder";
                    dockerInfo.GitRef = $"v{image.Tag}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "docker-compose-generator":
                    dockerInfo.DockerFilePath = "docker-compose-generator/linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = "docker-compose-generator/linuxarm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = "docker-compose-generator/linuxarm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/btcpayserver-docker";
                    dockerInfo.GitRef = $"dcg-latest";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "docker-bitcoingold":
                    dockerInfo.DockerFilePath = $"bitcoingold/{image.Tag}/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/Vutov/docker-bitcoin";
                    dockerInfo.GitRef = "master";
                    break;
                case "lightning":
                    dockerInfo.DockerFilePath = $"Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = "contrib/linuxarm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = "contrib/linuxarm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/lightning";
                    dockerInfo.GitRef = $"basedon-{image.Tag}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "groestlcoin/lightning":
                    dockerInfo.DockerFilePath = $"Dockerfile";
                    dockerInfo.GitLink = "https://github.com/Groestlcoin/lightning";
                    dockerInfo.GitRef = $"{image.Tag}";
                    break;
                case "lightning-charge":
                    dockerInfo.DockerFilePath = $"Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = "arm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = "arm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/ElementsProject/lightning-charge";
                    dockerInfo.GitRef = $"v{image.Tag.Replace("-standalone", "")}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "docker-bitcoinplus":
                    dockerInfo.DockerFilePath = $"bitcoinplus/{image.Tag}/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/ChekaZ/docker";
                    dockerInfo.GitRef = "master";
                    break;
                case "groestlcoin-lightning-charge":
                    dockerInfo.DockerFilePath = $"Dockerfile";
                    dockerInfo.GitLink = "https://github.com/Groestlcoin/groestlcoin-lightning-charge";
                    dockerInfo.GitRef = $"v{image.Tag.Substring("version-".Length)}";
                    break;
                case "groestlcoin-spark":
                    dockerInfo.DockerFilePath = $"Dockerfile";
                    dockerInfo.GitLink = "https://github.com/Groestlcoin/groestlcoin-spark";
                    dockerInfo.GitRef = $"v{image.Tag.Substring("version-".Length)}";
                    break;
                case "librepatron":
                    dockerInfo.DockerFilePath = $"Dockerfile";
                    dockerInfo.GitLink = "https://github.com/JeffVandrewJr/patron";
                    dockerInfo.GitRef = $"v{image.Tag}";
                    break;
                case "electrumx":
                    dockerInfo.DockerFilePath = $"Dockerfile";
                    dockerInfo.GitLink = "https://github.com/lukechilds/docker-electrumx";
                    dockerInfo.GitRef = $"master";
                    break;
                case "eclair":
                    dockerInfo.DockerFilePath = $"Dockerfile";
                    dockerInfo.GitLink = "https://github.com/ACINQ/eclair";
                    dockerInfo.GitRef = $"{image.Tag}";
                    break;
                case "groestlcoin/eclair":
                    dockerInfo.DockerFilePath = $"Dockerfile";
                    dockerInfo.GitLink = "https://github.com/Groestlcoin/eclair";
                    dockerInfo.GitRef = $"{image.Tag}";
                    break;
                case "isso":
                    dockerInfo.DockerFilePath = $"Dockerfile";
                    dockerInfo.GitLink = "https://github.com/JeffVandrewJr/isso";
                    dockerInfo.GitRef = $"patron.{image.Tag.Substring("atron.".Length)}";
                    break;
                case "docker-woocommerce":
                    dockerInfo.DockerFilePath = $"Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/docker-woocommerce";
                    dockerInfo.GitRef = $"v{image.Tag}";
                    break;
                case "mariadb":
                    dockerInfo.DockerFilePath = $"{image.Tag}/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/docker-library/mariadb";
                    dockerInfo.GitRef = $"master";
                    break;
                case "docker-trezarcoin":
                    dockerInfo.DockerFilePath = $"trezarcoin/1.2.0/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/ChekaZ/docker";
                    dockerInfo.GitRef = "master";
                    break;
                case "lnd":
                    dockerInfo.DockerFilePath = "linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = "linuxarm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = "linuxarm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/lnd";
                    dockerInfo.GitRef = $"basedon-{image.Tag}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "groestlcoin/lnd":
                    dockerInfo.DockerFilePath = "Dockerfile";
                    dockerInfo.GitLink = "https://github.com/Groestlcoin/lnd";
                    dockerInfo.GitRef = $"{image.Tag}";
                    dockerInfo.SupportedByUs = false;
                    break;
                case "monero":
                    dockerInfo.DockerFilePath = "Dockerfile";
                    dockerInfo.GitLink = "https://github.com/Kukks/monero-docker";
                    dockerInfo.GitRef = $"x86_64";
                    break;
                case "bitcoin":
                {
                    var tagNoRevision = image.Tag.Split('-').First();
                    dockerInfo.DockerFilePath = $"Bitcoin/{tagNoRevision}/linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"Bitcoin/{tagNoRevision}/linuxarm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = $"Bitcoin/{tagNoRevision}/linuxarm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/dockerfile-deps";
                    dockerInfo.GitRef = $"Bitcoin/{image.Tag}";
                    dockerInfo.SupportedByUs = true;
                    break;
                }
                case "elements":
                {
                    var tagNoRevision = image.Tag.Split('-').First();
                    dockerInfo.DockerFilePath = $"Elements/{tagNoRevision}/linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"Elements/{tagNoRevision}/linuxarm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = $"Elements/{tagNoRevision}/linuxarm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/dockerfile-deps";
                    dockerInfo.GitRef = $"Elements/{image.Tag}";
                    break;
                }
                case "tor":
                    dockerInfo.DockerFilePath = $"Tor/{image.Tag}/linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"Tor/{image.Tag}/linuxarm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = $"Tor/{image.Tag}/linuxarm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/dockerfile-deps";
                    dockerInfo.GitRef = $"Tor/{image.Tag}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "dash":
                    dockerInfo.DockerFilePath = $"Dash/{image.Tag}/linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"Dash/{image.Tag}/linuxarm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = $"Dash/{image.Tag}/linuxarm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/dockerfile-deps";
                    dockerInfo.GitRef = $"Dash/{image.Tag}";
                    break;
                case "argoneum":
                    dockerInfo.DockerFilePath = $"Argoneum/{image.Tag}/linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"Argoneum/{image.Tag}/linuxarm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = $"Argoneum/{image.Tag}/linuxarm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/dockerfile-deps";
                    dockerInfo.GitRef = $"Argoneum/{image.Tag}";
                    break;
                case "btcpayserver":
                    dockerInfo.DockerFilePath = "amd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = "arm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = "arm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/btcpayserver";
                    dockerInfo.GitRef = $"v{image.Tag}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "rtl":
                    dockerInfo.DockerFilePath = "Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = "Dockerfile.arm32v7";
                    dockerInfo.DockerFilePathARM64v8 = "Dockerfile.arm64v8";
                    dockerInfo.GitLink = "https://github.com/ShahanaFarooqui/RTL";
                    dockerInfo.GitRef = $"v{image.Tag}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "nbxplorer":
                    dockerInfo.DockerFilePath = "Dockerfile.linuxamd64";
                    dockerInfo.DockerFilePathARM32v7 = "Dockerfile.linuxarm32v7";
                    dockerInfo.DockerFilePathARM64v8 = "Dockerfile.linuxarm64v8";
                    dockerInfo.GitLink = "https://github.com/dgarage/nbxplorer";
                    dockerInfo.GitRef = $"v{image.Tag}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "btctransmuter":
                    dockerInfo.DockerFilePath = "Dockerfiles/amd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = "Dockerfiles/arm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = "Dockerfiles/arm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/btctransmuter";
                    dockerInfo.GitRef = $"v{image.Tag}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "dogecoin":
                    dockerInfo.DockerFilePath = $"dogecoin/{image.Tag}/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/rockstardev/docker-bitcoin";
                    dockerInfo.GitRef = "feature/dogecoin";
                    break;
                case "docker-bitcore":
                    dockerInfo.DockerFilePath = "btx-debian/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/dalijolijo/btcpayserver-docker-bitcore";
                    dockerInfo.GitRef = "master";
                    break;
                case "docker-feathercoin":
                    dockerInfo.DockerFilePath = $"feathercoin/{image.Tag}/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/ChekaZ/docker";
                    dockerInfo.GitRef = "master";
                    break;
                case "docker-groestlcoin":
                    dockerInfo.DockerFilePath = $"groestlcoin/{image.Tag}/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/NicolasDorier/docker-bitcoin";
                    dockerInfo.GitRef = "master";
                    break;
                case "docker-viacoin":
                    dockerInfo.DockerFilePath = $"viacoin/{image.Tag}/docker-viacoin";
                    dockerInfo.GitLink = "https://github.com/viacoin/docker-viacoin";
                    dockerInfo.GitRef = "master";
                    break;
                case "litecoin":
                    dockerInfo.DockerFilePath = $"Litecoin/{NoRevision(image.Tag)}/linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"Litecoin/{NoRevision(image.Tag)}/linuxarm32v7.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/dockerfile-deps";
                    dockerInfo.GitRef = $"Litecoin/{image.Tag}";
                    break;
                case "docker-monacoin":
                    dockerInfo.DockerFilePath = $"monacoin/{image.Tag}/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/wakiyamap/docker-bitcoin";
                    dockerInfo.GitRef = "master";
                    break;
                case "nginx":
                    dockerInfo.DockerFilePath = $"stable/stretch/Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"stable/stretch/Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = $"stable/stretch/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/nginxinc/docker-nginx";
                    dockerInfo.GitRef = image.Tag;
                    dockerInfo.SupportedByUs = true;
                    break;
                case "docker-gen":
                    dockerInfo.DockerFilePath = $"linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"linuxarm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = $"linuxarm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/docker-gen";
                    dockerInfo.GitRef = $"v{image.Tag}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "letsencrypt-nginx-proxy-companion":
                    dockerInfo.DockerFilePath = $"linuxamd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"linuxarm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = $"linuxarm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/docker-letsencrypt-nginx-proxy-companion";
                    dockerInfo.GitRef = $"v{image.Tag}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "btcqbo":
                    dockerInfo.DockerFilePath = $"Dockerfile";
                    dockerInfo.GitLink = "https://github.com/JeffVandrewJr/btcqbo";
                    dockerInfo.GitRef = $"v{image.Tag}";
                    break;
                case "redis":
                    dockerInfo.DockerFilePath = $"5.0/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/docker-library/redis";
                    dockerInfo.GitRef = $"f1a8498333ae3ab340b5b39fbac1d7e1dc0d628c";
                    break;
                case "postgres":
                    dockerInfo.DockerFilePath = $"9.6/Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"9.6/Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = $"9.6/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/docker-library/postgres";
                    dockerInfo.GitRef = $"b7cb3c6eacea93be2259381033be3cc435649369";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "traefik":
                    dockerInfo.DockerFilePath = $"scratch/Dockerfile";
                    dockerInfo.GitLink = "https://github.com/containous/traefik-library-image";
                    dockerInfo.GitRef = $"master";
                    break;
                case "spark-wallet":
                    dockerInfo.DockerFilePath = $"Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"arm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = $"arm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/shesek/spark-wallet";
                    dockerInfo.GitRef = $"v{image.Tag.Split('-')[0]}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "c-lightning-rest":
                    dockerInfo.DockerFilePath = $"amd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"arm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = $"arm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/Ride-The-Lightning/c-lightning-REST";
                    dockerInfo.GitRef = $"v{image.Tag.Split('-')[0]}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "btcpayserver-configurator":
                    dockerInfo.DockerFilePath = $"Dockerfiles/amd64.Dockerfile";
                    dockerInfo.DockerFilePathARM32v7 = $"Dockerfiles/arm32v7.Dockerfile";
                    dockerInfo.DockerFilePathARM64v8 = $"Dockerfiles/arm64v8.Dockerfile";
                    dockerInfo.GitLink = "https://github.com/btcpayserver/btcpayserver-configurator";
                    dockerInfo.GitRef = $"v{image.Tag.Split('-')[0]}";
                    dockerInfo.SupportedByUs = true;
                    break;
                case "thunderhub":
                    dockerInfo.DockerFilePath = $"Dockerfile";
                    dockerInfo.GitLink = "https://github.com/apotdevin/thunderhub";
                    dockerInfo.GitRef = $"{image.Tag.Split('-')[0]}";
                    dockerInfo.SupportedByUs = false;
                    break;
                default:
                    if (firstTry)
                    {
                        name = $"{image.Name}";
                        firstTry = false;
                        goto retry;
                    }
                    else
                        return null;
            }
            dockerInfo.DockerHubLink = image.DockerHubLink;
            dockerInfo.Image = image;
            return dockerInfo;
        }
        string NoRevision(string str)
        {
            return str.Split('-').First();
        }
        private YamlMappingNode ParseDocument(string fragment)
        {
            var input = new StringReader(File.ReadAllText(fragment));
            YamlStream stream = new YamlStream();
            stream.Load(input);
            return (YamlMappingNode)stream.Documents[0].RootNode;
        }

        private static void DeleteDirectory(string outputDirectory)
        {
            try
            {
                Directory.Delete(outputDirectory, true);
            }
            catch
            {
            }
        }

        private static string FindRoot(string rootDirectory)
        {
            string directory = Directory.GetCurrentDirectory();
            int i = 0;
            while (true)
            {
                if (i > 10)
                    throw new DirectoryNotFoundException(rootDirectory);
                if (directory.EndsWith(rootDirectory))
                    return directory;
                directory = Path.GetFullPath(Path.Combine(directory, ".."));
                i++;
            }
        }
    }
}
