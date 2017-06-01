﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;

namespace Clarius.TransformOnBuild.MSBuild.Task
{
    public class TransformOnBuildTask : Microsoft.Build.Utilities.Task
    {
        private const BindingFlags BindingFlags = System.Reflection.BindingFlags.NonPublic |
                                                  System.Reflection.BindingFlags.FlattenHierarchy |
                                                  System.Reflection.BindingFlags.Instance |
                                                  System.Reflection.BindingFlags.Public;

        private string _commonProgramFiles;
        private ProjectInstance _projectInstance;
        private Dictionary<string, string> _properties;
        private string _textTransformPath;
        private string _transformExe;

        public override bool Execute()
        {
            _projectInstance = GetProjectInstance();
            _properties = _projectInstance.Properties.ToDictionary(p => p.Name, p => p.EvaluatedValue);

            InitPathProperties();

            if (!File.Exists(_transformExe))
            {
                Log.LogError("Failed to find TextTransform.exe tool at '{0}'.", _transformExe);
                return false;
            }

            var textTransform = _projectInstance.Items.Where(i =>
            {
                var comparer = StringComparer.InvariantCultureIgnoreCase;
                return (comparer.Equals(i.ItemType, "None") || comparer.Equals(i.ItemType, "Content")) &&
                       comparer.Equals(i.GetMetadataValue("Generator"), "TextTemplatingFileGenerator");
            });

            foreach (var templateItem in textTransform)
            {
                var templatePath = templateItem.GetMetadataValue("FullPath");
                var templateBackupPath = templatePath + ".bak_clarius";
                var isTemplatePathReadOnly = false;
                try
                {
                    isTemplatePathReadOnly = (File.GetAttributes(templatePath) & FileAttributes.ReadOnly) ==
                                             FileAttributes.ReadOnly;
                    if (isTemplatePathReadOnly)
                        File.SetAttributes(templatePath, ~FileAttributes.ReadOnly);
                    File.Copy(templatePath, templateBackupPath, true);

                    RewriteTemplateFile(templatePath);

                    var result = RunTransformTool(templatePath);

                    if (!result)
                        return false;
                }
                finally
                {
                    File.Copy(templateBackupPath, templatePath, true);
                    File.Delete(templateBackupPath);
                    if (isTemplatePathReadOnly)
                        File.SetAttributes(templatePath, FileAttributes.ReadOnly);
                }
            }


            return true;
        }

        private void RewriteTemplateFile(string templatePath)
        {
            var encoding = GetCurrentEncoding(templatePath);
            var template = File.ReadAllText(templatePath, encoding);
            template = RewriteTemplateContent(template);
            File.WriteAllText(templatePath, template, encoding);
        }

        private static Encoding GetCurrentEncoding(string templatePath)
        {
            using (var streamReader = new StreamReader(templatePath))
            {
                streamReader.ReadLine();
                return streamReader.CurrentEncoding;
            }
        }

        private string RewriteTemplateContent(string template)
        {
            var result = Regex.Replace(template,
                @"(?im)(<#@\s*assembly\s+name\s*=\s*"".*?""|<#@\s*include\s+file\s*=\s*"".*?"")",
                m => ExpandVariables(m.Value));
            return result;
        }

        private string ExpandVariables(string str)
        {
            var result = Environment.ExpandEnvironmentVariables(str);
            result = Regex.Replace(result, @"\$\((?<PropertyName>.+?)\)",
                m => GetPropertyValue(m.Groups["PropertyName"].Value, true));
            return result;
        }

        private bool RunTransformTool(string templatePath)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _transformExe,
                    Arguments = $"\"{templatePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    UseShellExecute = false
                }
            };
            process.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data == null)
                    return;
                Log.LogMessageFromText(args.Data, MessageImportance.Normal);
            };
            process.OutputDataReceived += (sender, args) =>
            {
                if (args.Data == null)
                    return;
                Log.LogMessageFromText(args.Data, MessageImportance.Low);
            };
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            process.WaitForExit();
            return process.ExitCode == 0;
        }

        private void InitPathProperties()
        {
            _commonProgramFiles = Environment.GetEnvironmentVariable("CommonProgramFiles(x86)");
            if (string.IsNullOrEmpty(_commonProgramFiles))
            {
                _commonProgramFiles = GetPropertyValue("CommonProgramFiles");
            }

            _textTransformPath = GetPropertyValue("TextTransformPath");
            if (string.IsNullOrEmpty(_textTransformPath))
                _textTransformPath =
                    $@"{_commonProgramFiles}\Microsoft Shared\TextTemplating\{
                            GetPropertyValue("VisualStudioVersion")
                        }\TextTransform.exe";

            // Initial default value
            _transformExe = _textTransformPath;

            // Cascading probing if file not found
            if (!File.Exists(_transformExe))
                _transformExe = $@"{_commonProgramFiles}\Microsoft Shared\TextTemplating\10.0\TextTransform.exe";
            if (!File.Exists(_transformExe))
                _transformExe = $@"{_commonProgramFiles}\Microsoft Shared\TextTemplating\11.0\TextTransform.exe";
            if (!File.Exists(_transformExe))
                _transformExe = $@"{_commonProgramFiles}\Microsoft Shared\TextTemplating\12.0\TextTransform.exe";
            // Future proof 'til VS2013+2
            if (!File.Exists(_transformExe))
                _transformExe = $@"{_commonProgramFiles}\Microsoft Shared\TextTemplating\13.0\TextTransform.exe";
            if (!File.Exists(_transformExe))
                _transformExe = $@"{_commonProgramFiles}\Microsoft Shared\TextTemplating\14.0\TextTransform.exe";
        }

        /// <summary>
        ///     Inspired by
        ///     http://stackoverflow.com/questions/3043531/when-implementing-a-microsoft-build-utilities-task-how-to-i-get-access-to-the-va
        /// </summary>
        /// <returns></returns>
        private ProjectInstance GetProjectInstance()
        {
            var buildEngineType = BuildEngine.GetType();
            var targetBuilderCallbackField = buildEngineType.GetField("targetBuilderCallback", BindingFlags) ??
                                             buildEngineType.GetField("_targetBuilderCallback", BindingFlags);
            if (targetBuilderCallbackField == null)
                throw new Exception("Could not extract targetBuilderCallback from " + buildEngineType.FullName);
            var targetBuilderCallback = targetBuilderCallbackField.GetValue(BuildEngine);
            var targetCallbackType = targetBuilderCallback.GetType();
            var projectInstanceField = targetCallbackType.GetField("projectInstance", BindingFlags) ??
                                       targetCallbackType.GetField("_projectInstance", BindingFlags);
            if (projectInstanceField == null)
                throw new Exception("Could not extract projectInstance from " + targetCallbackType.FullName);
            return (ProjectInstance) projectInstanceField.GetValue(targetBuilderCallback);
        }

        private string GetPropertyValue(string propertyName, bool throwIfNotFound = false)
        {
            string propertyValue;
            if (_properties.TryGetValue(propertyName, out propertyValue))
                return propertyValue;
            if (throwIfNotFound)
                throw new Exception($"Could not resolve property $({propertyName})");
            return "";
        }
    }
}