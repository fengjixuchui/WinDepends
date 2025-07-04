﻿/*******************************************************************************
*
*  (C) COPYRIGHT AUTHORS, 2024 - 2025
*
*  TITLE:       CCORECLIENT.CS
*
*  VERSION:     1.00
*
*  DATE:        22 Jun 2025
*  
*  Core Server communication class.
*
* THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
* ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED
* TO THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
* PARTICULAR PURPOSE.
*
*******************************************************************************/
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Serialization.Json;
using System.Text;

namespace WinDepends;

public enum ModuleInformationType
{
    Headers,
    Imports,
    Exports,
    DataDirectories,
    ApiSetName
}

public enum ServerErrorStatus
{
    NoErrors = 0,
    ServerNeedRestart,
    NetworkStreamNotInitialized,
    SocketException,
    GeneralException
}

public enum ModuleOpenStatus
{
    Okay,
    ErrorUnspecified,
    ErrorSendCommand,
    ErrorReceivedDataInvalid,
    ErrorFileNotFound,
    ErrorFileNotMapped,
    ErrorCannotReadFileHeaders,
    ErrorInvalidHeadersOrSignatures
}

public enum CCoreClientSerializerType : int
{
    Headers = 0,
    Imports,
    Exports,
    DataDirectories,
    ResolvedFileName,
    ApiSetNamespace,
    CallStats,
    KnownDlls,
    FileInformation,
    Exception
}

public class CBufferChain
{
    private CBufferChain _next;
    public uint DataSize;
    public char[] Data;

    public CBufferChain Next { get => _next; set => _next = value; }

    public CBufferChain()
    {
        Data = new char[CConsts.CoreServerChainSizeMax];
    }

    public string BufferToString()
    {
        var sb = new StringBuilder();
        var chain = this;

        do
        {
            if (chain.Data is { Length: > 0 } data)
            {
                // Find last non-null character index
                int length = data.Length;
                while (length > 0 && data[length - 1] == '\0')
                    length--;

                // Process characters
                for (int i = 0; i < length; i++)
                {
                    char c = data[i];
                    if (c is not ('\n' or '\r'))
                        sb.Append(c);
                }
            }
            chain = chain.Next;
        } while (chain != null);

        return sb.ToString();
    }
}

public class CCoreClient : IDisposable
{
    private bool _disposed;
    private Process _serverProcess;     // WinDepends.Core instance.
    private TcpClient _clientConnection;
    private NetworkStream _dataStream;
    private readonly AddLogMessageCallback _addLogMessage;
    private string _serverApplication;
    public TcpClient ClientConnection => _clientConnection;
    public string IPAddress { get; }
    public int Port { get; set; }
    private readonly DataContractJsonSerializer[] _serializers;

    private const int CORE_CONNECTION_TIMEOUT = 3000;
    private const int CORE_NETWORK_TIMEOUT = 5000;

    private static readonly HashSet<string> s_forbiddenKernelLibs = new(StringComparer.OrdinalIgnoreCase)
    {
        CConsts.NtdllDll,
        CConsts.Kernel32Dll
    };

    private static readonly HashSet<string> s_requiredKernelLibs = new(StringComparer.OrdinalIgnoreCase)
    {
        CConsts.NtoskrnlExe,
        CConsts.HalDll,
        CConsts.KdComDll,
        CConsts.BootVidDll
    };

    public ServerErrorStatus ErrorStatus { get; set; }

    public int ServerProcessId => _serverProcess?.Id ?? -1;

    public string GetServerApplication()
    {
        return _serverApplication;
    }

    public void SetServerApplication(string value)
    {
        _serverApplication = value;
    }

    public CCoreClient(string serverApplication, string ipAddress,
                       AddLogMessageCallback logMessageCallback)
    {
        _addLogMessage = logMessageCallback ?? throw new ArgumentNullException(nameof(logMessageCallback));
        SetServerApplication(serverApplication);
        IPAddress = ipAddress;
        ErrorStatus = ServerErrorStatus.NoErrors;

        _serializers = new DataContractJsonSerializer[10];
        _serializers[(int)CCoreClientSerializerType.Headers] = new DataContractJsonSerializer(typeof(CCoreImageHeaders));
        _serializers[(int)CCoreClientSerializerType.Imports] = new DataContractJsonSerializer(typeof(CCoreImports));
        _serializers[(int)CCoreClientSerializerType.Exports] = new DataContractJsonSerializer(typeof(CCoreExports));
        _serializers[(int)CCoreClientSerializerType.DataDirectories] = new DataContractJsonSerializer(typeof(CCoreDirectoryEntry));
        _serializers[(int)CCoreClientSerializerType.ResolvedFileName] = new DataContractJsonSerializer(typeof(CCoreResolvedFileName));
        _serializers[(int)CCoreClientSerializerType.ApiSetNamespace] = new DataContractJsonSerializer(typeof(CCoreApiSetNamespaceInfo));
        _serializers[(int)CCoreClientSerializerType.CallStats] = new DataContractJsonSerializer(typeof(CCoreCallStats));
        _serializers[(int)CCoreClientSerializerType.KnownDlls] = new DataContractJsonSerializer(typeof(CCoreKnownDlls));
        _serializers[(int)CCoreClientSerializerType.FileInformation] = new DataContractJsonSerializer(typeof(CCoreFileInformation));
        _serializers[(int)CCoreClientSerializerType.Exception] = new DataContractJsonSerializer(typeof(CCoreException));
    }

    protected void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }
        if (disposing)
        {
            DisconnectClient();
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Checks if the server reply indicate success.
    /// </summary>
    /// <returns></returns>
    public bool IsRequestSuccessful(CModule module = null)
    {
        CBufferChain idata = ReceiveReply();
        if (IsNullOrEmptyResponse(idata))
        {
            return false;
        }

        string response = new(idata.Data, 0, (int)Math.Min(idata.DataSize, idata.Data.Length));
        if (string.Equals(response, CConsts.WDEP_STATUS_200, StringComparison.Ordinal))
        {
            return true;
        }

        if (response.StartsWith(CConsts.WDEP_STATUS_600, StringComparison.Ordinal))
        {
            CheckExceptionInReply(module);
        }
        return false;
    }

    public static bool IsModuleNameApiSetContract(string moduleName)
    {
        return moduleName?.Length >= 4 &&
              (moduleName.StartsWith("API-", StringComparison.OrdinalIgnoreCase) ||
               moduleName.StartsWith("EXT-", StringComparison.OrdinalIgnoreCase));
    }

    public void OutputException(CModule module, string reply)
    {
        var ex = (CCoreException)DeserializeDataJSON(typeof(CCoreException), reply);
        if (ex == null) return;

        var locations = new[] { "image headers", "data directories", "imports", "exports" };
        var location = ex.Location >= 0 && ex.Location < locations.Length ? locations[ex.Location] : string.Empty;

        if (module != null)
            module.OtherErrorsPresent = true;

        var moduleName = module?.FileName != null ? Path.GetFileName(module.FileName) : string.Empty;
        var exceptionText = PeExceptionHelper.TranslateExceptionCode(ex.Code);

        _addLogMessage(
            $"An exception {exceptionText} 0x{ex.Code:X8} occured while processing {location}" +
            (!string.IsNullOrEmpty(moduleName) ? $" of {moduleName}" : ""),
            LogMessageType.ErrorOrWarning);
    }

    public void CheckExceptionInReply(CModule module)
    {
        CBufferChain idata = ReceiveReply();
        if (!IsNullOrEmptyResponse(idata))
        {
            var reply = idata.BufferToString();
            if (!string.IsNullOrEmpty(reply))
            {
                OutputException(module, reply);
            }
        }
    }

    public object SendCommandAndReceiveReplyAsObjectJSON(string command, Type objectType, CModule module,
                                                         bool preProcessData = false)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CCoreClient));

        if (!SendRequest(command))
        {
            return null;
        }

        if (!IsRequestSuccessful(module))
        {
            return null;
        }

        CBufferChain idata = ReceiveReply();
        if (IsNullOrEmptyResponse(idata))
        {
            return null;
        }

        string result = idata.BufferToString();
        if (string.IsNullOrEmpty(result))
        {
            return null;
        }

        if (preProcessData)
        {
            result = result.Replace("\\", "\\\\");
        }

        return DeserializeDataJSON(module?.FileName, objectType, result);
    }

    /// <summary>
    /// Send command to depends-core
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    private bool SendRequest(string message)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CCoreClient));

        // Communication failure, server need restart.
        if (_clientConnection == null || !_clientConnection.Connected)
        {
            ErrorStatus = ServerErrorStatus.ServerNeedRestart;
            return false;
        }

        if (_dataStream == null)
        {
            ErrorStatus = ServerErrorStatus.NetworkStreamNotInitialized;
            return false;
        }

        try
        {
            using (BinaryWriter bw = new(_dataStream, Encoding.Unicode, true))
            {
                bw.Write(Encoding.Unicode.GetBytes(message));
            }
        }
        catch (Exception ex)
        {
            _addLogMessage($"Failed to send data to the server, error message: {ex.Message}", LogMessageType.ErrorOrWarning);
            ErrorStatus = (ex is IOException) ? ServerErrorStatus.SocketException : ServerErrorStatus.GeneralException;
            return false;
        }

        ErrorStatus = ServerErrorStatus.NoErrors;
        return true;
    }

    /// <summary>
    /// Receive reply from depends-core and store it into temporary object.
    /// </summary>
    /// <returns></returns>
    private CBufferChain ReceiveReply()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CCoreClient));

        if (_dataStream == null)
        {
            ErrorStatus = ServerErrorStatus.NetworkStreamNotInitialized;
            return null;
        }

        try
        {
            using (BinaryReader br = new(_dataStream, Encoding.Unicode, true))
            {
                CBufferChain bufferChain = new(), currentBuffer;
                char previousChar = '\0';
                currentBuffer = bufferChain;

                while (true)
                {
                    for (int i = 0; i < CConsts.CoreServerChainSizeMax; i++)
                    {
                        try
                        {
                            bufferChain.Data[i] = br.ReadChar();
                        }
                        catch
                        {
                            currentBuffer.DataSize = (uint)i;
                            return currentBuffer;
                        }

                        bufferChain.DataSize++;

                        if (bufferChain.Data[i] == '\n' && previousChar == '\r')
                        {
                            return currentBuffer;
                        }

                        previousChar = bufferChain.Data[i];
                    }

                    bufferChain.Next = new();
                    bufferChain = bufferChain.Next;
                }
            }
        }
        catch (Exception ex)
        {
            _addLogMessage($"Receive data failed. Server message: {ex.Message}", LogMessageType.ErrorOrWarning);
            ErrorStatus = ServerErrorStatus.GeneralException;
        }
        return null;
    }

    private DataContractJsonSerializer GetSerializerForType(Type objectType)
    {
        if (objectType == typeof(CCoreImageHeaders))
            return _serializers[(int)CCoreClientSerializerType.Headers];
        if (objectType == typeof(CCoreImports))
            return _serializers[(int)CCoreClientSerializerType.Imports];
        if (objectType == typeof(CCoreExports))
            return _serializers[(int)CCoreClientSerializerType.Exports];
        if (objectType == typeof(CCoreDirectoryEntry))
            return _serializers[(int)CCoreClientSerializerType.DataDirectories];
        if (objectType == typeof(CCoreResolvedFileName))
            return _serializers[(int)CCoreClientSerializerType.ResolvedFileName];
        if (objectType == typeof(CCoreApiSetNamespaceInfo))
            return _serializers[(int)CCoreClientSerializerType.ApiSetNamespace];
        if (objectType == typeof(CCoreCallStats))
            return _serializers[(int)CCoreClientSerializerType.CallStats];
        if (objectType == typeof(CCoreKnownDlls))
            return _serializers[(int)CCoreClientSerializerType.KnownDlls];
        if (objectType == typeof(CCoreFileInformation))
            return _serializers[(int)CCoreClientSerializerType.FileInformation];
        if (objectType == typeof(CCoreException))
            return _serializers[(int)CCoreClientSerializerType.Exception];

        // Fallback for unknown types
        return new DataContractJsonSerializer(objectType);
    }

    object DeserializeDataJSON(string FileName, Type objectType, string data)
    {
        if (string.IsNullOrEmpty(data))
            return null;

        try
        {
            // Try to find pre-created serializer
            DataContractJsonSerializer serializer = GetSerializerForType(objectType);
            using MemoryStream ms = new(Encoding.Unicode.GetBytes(data));
            return serializer.ReadObject(ms);
        }
        catch (Exception ex)
        {
            _addLogMessage($"Data deserialization failed: {ex.Message}", LogMessageType.ErrorOrWarning);
            _addLogMessage($"Failed to analyze {FileName}", LogMessageType.ErrorOrWarning);
            return null;
        }
    }

    object DeserializeDataJSON(Type objectType, string data)
    {
        if (string.IsNullOrEmpty(data))
            return null;

        try
        {
            // Try to find pre-created serializer
            DataContractJsonSerializer serializer = GetSerializerForType(objectType);
            using MemoryStream ms = new(Encoding.Unicode.GetBytes(data));
            return serializer.ReadObject(ms);
        }
        catch (Exception ex)
        {
            _addLogMessage($"Data deserialization failed: {ex.Message}", LogMessageType.ErrorOrWarning);
            return null;
        }
    }

    public static bool IsNullOrEmptyResponse(CBufferChain buffer)
    {
        // Check for null.
        if (buffer == null || buffer.DataSize == 0 || buffer.Data == null)
        {
            return true;
        }

        // Check for empty.
        if (buffer.DataSize == 2)
        {
            return buffer.Data[0] == '\r' && buffer.Data[1] == '\n';
        }
        return false;
    }

    private static ModuleOpenStatus SetModuleError(CModule module, bool fileNotFound, bool invalid, ModuleOpenStatus status)
    {
        if (fileNotFound)
            module.FileNotFound = true;

        if (invalid)
            module.IsInvalid = true;

        return status;
    }

    /// <summary>
    /// Open Coff module and read for futher operations.
    /// </summary>
    public ModuleOpenStatus OpenModule(ref CModule module, bool useStats, bool processRelocs, bool useCustomImageBase, uint customImageBase)
    {
        if (module == null)
            throw new ArgumentNullException(nameof(module));

        if (_disposed)
            throw new ObjectDisposedException(nameof(CCoreClient));

        string cmd = $"open file \"{module.FileName}\"";

        if (useStats)
        {
            cmd += " use_stats";
        }

        if (processRelocs)
        {
            cmd += $" process_relocs";
        }

        if (useCustomImageBase)
        {
            cmd += $" custom_image_base {customImageBase}";
        }

        cmd += "\r\n";

        if (!SendRequest(cmd))
        {
            return ModuleOpenStatus.ErrorSendCommand;
        }

        CBufferChain idata = ReceiveReply();
        if (IsNullOrEmptyResponse(idata))
        {
            return ModuleOpenStatus.ErrorReceivedDataInvalid;
        }

        string response = new(idata.Data, 0, (int)Math.Min(idata.DataSize, idata.Data.Length));

        if (!string.Equals(response, CConsts.WDEP_STATUS_200, StringComparison.Ordinal))
        {
            return response switch
            {
                var s when string.Equals(s, CConsts.WDEP_STATUS_404, StringComparison.Ordinal) =>
                   SetModuleError(module, true, false, ModuleOpenStatus.ErrorFileNotFound),

                var s when string.Equals(s, CConsts.WDEP_STATUS_403, StringComparison.Ordinal) =>
                    SetModuleError(module, false, true, ModuleOpenStatus.ErrorCannotReadFileHeaders),

                var s when string.Equals(s, CConsts.WDEP_STATUS_415, StringComparison.Ordinal) =>
                    SetModuleError(module, false, true, ModuleOpenStatus.ErrorInvalidHeadersOrSignatures),

                var s when string.Equals(s, CConsts.WDEP_STATUS_502, StringComparison.Ordinal) =>
                   SetModuleError(module, false, true, ModuleOpenStatus.ErrorFileNotMapped),

                _ => SetModuleError(module, false, true, ModuleOpenStatus.ErrorUnspecified)
            };
        }

        idata = ReceiveReply();
        if (IsNullOrEmptyResponse(idata))
        {
            return ModuleOpenStatus.ErrorReceivedDataInvalid;
        }

        response = idata.BufferToString();
        if (string.IsNullOrEmpty(response))
        {
            return ModuleOpenStatus.ErrorReceivedDataInvalid;
        }

        var fileInformation = (CCoreFileInformation)DeserializeDataJSON(typeof(CCoreFileInformation), response);
        if (fileInformation != null)
        {
            module.ModuleData.Attributes = (FileAttributes)fileInformation.FileAttributes;
            module.ModuleData.RealChecksum = fileInformation.RealChecksum;
            module.ModuleData.ImageFixed = fileInformation.ImageFixed;
            module.ModuleData.ImageDotNet = fileInformation.ImageDotNet;
            module.ModuleData.FileSize = fileInformation.FileSizeLow | ((ulong)fileInformation.FileSizeHigh << 32);

            long fileTime = ((long)fileInformation.LastWriteTimeHigh << 32) | fileInformation.LastWriteTimeLow;
            module.ModuleData.FileTimeStamp = DateTime.FromFileTime(fileTime);

            return ModuleOpenStatus.Okay;
        }

        return ModuleOpenStatus.ErrorUnspecified;
    }

    /// <summary>
    /// Close previously opened module.
    /// </summary>
    public bool CloseModule()
    {
        return SendRequest("close\r\n");
    }

    public bool ExitRequest()
    {
        return SendRequest("exit\r\n");
    }

    public bool ShutdownRequest()
    {
        return SendRequest("shutdown\r\n");
    }

    public object GetModuleInformationByType(ModuleInformationType moduleInformationType, CModule module, string parameters = null)
    {
        string cmd;
        bool preProcessData = false;
        Type objectType;

        switch (moduleInformationType)
        {
            case ModuleInformationType.Headers:
                cmd = "headers\r\n";
                objectType = typeof(CCoreImageHeaders);
                break;
            case ModuleInformationType.Imports:
                preProcessData = true;
                cmd = "imports\r\n";
                objectType = typeof(CCoreImports);
                break;
            case ModuleInformationType.Exports:
                preProcessData = true;
                cmd = "exports\r\n";
                objectType = typeof(CCoreExports);
                break;
            case ModuleInformationType.DataDirectories:
                cmd = "datadirs\r\n";
                objectType = typeof(CCoreDirectoryEntry);
                break;
            case ModuleInformationType.ApiSetName:
                cmd = $"apisetresolve {parameters}\r\n";
                objectType = typeof(CCoreResolvedFileName);
                break;
            default:
                return null;
        }

        return SendCommandAndReceiveReplyAsObjectJSON(cmd, objectType, module, preProcessData);
    }

    public List<CCoreDirectoryEntry> GetModuleDataDirectories(CModule module)
    {
        return (List<CCoreDirectoryEntry>)GetModuleInformationByType(ModuleInformationType.DataDirectories, module);
    }

    public CCoreApiSetNamespaceInfo GetApiSetNamespaceInfo()
    {
        return (CCoreApiSetNamespaceInfo)SendCommandAndReceiveReplyAsObjectJSON(
            "apisetnsinfo\r\n", typeof(CCoreApiSetNamespaceInfo), null);
    }

    public CCoreCallStats GetCoreCallStats()
    {
        return (CCoreCallStats)SendCommandAndReceiveReplyAsObjectJSON(
              "callstats\r\n", typeof(CCoreCallStats), null);
    }

    public bool GetModuleHeadersInformation(CModule module)
    {
        if (module == null)
        {
            return false;
        }

        var fh = (CCoreImageHeaders)GetModuleInformationByType(ModuleInformationType.Headers, module);
        if (fh == null)
        {
            return false;
        }

        CModuleData moduleData = module.ModuleData;

        // Set various module data properties
        moduleData.LinkerVersion = $"{fh.OptionalHeader.MajorLinkerVersion}.{fh.OptionalHeader.MinorLinkerVersion}";
        moduleData.SubsystemVersion = $"{fh.OptionalHeader.MajorSubsystemVersion}.{fh.OptionalHeader.MinorSubsystemVersion}";
        moduleData.ImageVersion = $"{fh.OptionalHeader.MajorImageVersion}.{fh.OptionalHeader.MinorImageVersion}";
        moduleData.OSVersion = $"{fh.OptionalHeader.MajorOperatingSystemVersion}.{fh.OptionalHeader.MinorOperatingSystemVersion}";
        moduleData.LinkChecksum = fh.OptionalHeader.CheckSum;
        moduleData.Machine = fh.FileHeader.Machine;
        moduleData.LinkTimeStamp = fh.FileHeader.TimeDateStamp;
        moduleData.Characteristics = fh.FileHeader.Characteristics;
        moduleData.Subsystem = fh.OptionalHeader.Subsystem;
        moduleData.VirtualSize = fh.OptionalHeader.SizeOfImage;
        moduleData.PreferredBase = fh.OptionalHeader.ImageBase;
        moduleData.DllCharacteristics = fh.OptionalHeader.DllCharacteristics;
        moduleData.ExtendedCharacteristics = fh.ExtendedDllCharacteristics;

        if (fh.FileVersion != null)
        {
            moduleData.FileVersion = $"{fh.FileVersion.FileVersionMS.HIWORD()}." +
                $"{fh.FileVersion.FileVersionMS.LOWORD()}." +
                $"{fh.FileVersion.FileVersionLS.HIWORD()}." +
                $"{fh.FileVersion.FileVersionLS.LOWORD()}";

            moduleData.ProductVersion = $"{fh.FileVersion.ProductVersionMS.HIWORD()}." +
                $"{fh.FileVersion.ProductVersionMS.LOWORD()}." +
                $"{fh.FileVersion.ProductVersionLS.HIWORD()}." +
                $"{fh.FileVersion.ProductVersionLS.LOWORD()}";
        }
        else
        {
            moduleData.FileVersion = "N/A";
            moduleData.ProductVersion = "N/A";
        }

        //
        // Remember debug directory types.
        //
        if (fh.DebugDirectory != null)
        {
            foreach (var entry in fh.DebugDirectory)
            {
                moduleData.DebugDirTypes.Add(entry.Type);
                if (entry.Type == (uint)DebugEntryType.Reproducible)
                {
                    module.IsReproducibleBuild = true;
                }
            }
        }

        if (!string.IsNullOrEmpty(fh.Base64Manifest))
        {
            module.ManifestData = fh.Base64Manifest;
        }

        return true;
    }

    private static void CheckIfKernelModule(CModule module, CCoreImports imports)
    {
        // Skip check if already determined to be a kernel module
        if (module.IsKernelModule ||
            module.ModuleData.Subsystem != NativeMethods.IMAGE_SUBSYSTEM_NATIVE)
            return;

        bool hasForbiddenLibrary = false;
        bool hasRequiredLibrary = false;

        foreach (var entry in imports.Library)
        {
            // Check for forbidden user-mode DLL's
            if (!hasForbiddenLibrary && s_forbiddenKernelLibs.Contains(entry.Name))
            {
                hasForbiddenLibrary = true;
                break;
            }

            // Check for required kernel-mode components
            if (!hasRequiredLibrary && s_requiredKernelLibs.Contains(entry.Name))
            {
                hasRequiredLibrary = true;
            }
        }

        // Module is kernel-mode if it has required libraries but no forbidden ones
        if (!hasForbiddenLibrary && hasRequiredLibrary)
        {
            module.IsKernelModule = true;
        }
    }

    public void ProcessImports(CModule module,
                               bool DelayLibraries,
                               List<CCoreImportLibrary> LibraryList,
                               List<SearchOrderType> searchOrderUM,
                               List<SearchOrderType> searchOrderKM,
                               Dictionary<int, FunctionHashObject> parentImportsHashTable)
    {
        foreach (var entry in LibraryList)
        {
            string moduleName = entry.Name;
            string rawModuleName = entry.Name;

            bool isApiSetContract = IsModuleNameApiSetContract(moduleName);

            if (isApiSetContract)
            {
                string cachedName = CApiSetCacheManager.GetResolvedNameByApiSetName(moduleName);

                if (cachedName == null)
                {
                    var resolvedName = (CCoreResolvedFileName)GetModuleInformationByType(ModuleInformationType.ApiSetName,
                        module, moduleName);

                    if (resolvedName != null)
                    {
                        CApiSetCacheManager.AddApiSet(moduleName, resolvedName.Name);
                        moduleName = resolvedName.Name;
                    }
                }
                else
                {
                    moduleName = cachedName;
                }

            }

            var moduleFileName = CPathResolver.ResolvePathForModule(moduleName,
                                                                    module,
                                                                    searchOrderUM,
                                                                    searchOrderKM,
                                                                    out SearchOrderType resolvedBy);

            if (!string.IsNullOrEmpty(moduleFileName))
            {
                moduleName = moduleFileName;
            }

            CModule dependent = new(moduleName, rawModuleName, resolvedBy, isApiSetContract)
            {
                IsDelayLoad = DelayLibraries,
                IsKernelModule = module.IsKernelModule, //propagate from parent
            };

            module.Dependents.Add(dependent);

            foreach (var func in entry.Function)
            {
                dependent.ParentImports.Add(new CFunction(func));

                FunctionHashObject funcHashObject = new(dependent.FileName, func.Name, func.Ordinal);
                var uniqueKey = funcHashObject.GenerateUniqueKey();
                parentImportsHashTable.TryAdd(uniqueKey, funcHashObject);
            }
        }
    }

    private void HandleImportExceptions(CCoreImports imports)
    {
        bool exceptStd = (imports.Exception & 1) != 0;
        bool exceptDelay = (imports.Exception & 2) != 0;

        if (exceptStd && exceptDelay)
        {
            _addLogMessage(
                $"Exceptions occurred while processing imports:\n" +
                $"  Standard: {PeExceptionHelper.TranslateExceptionCode(imports.ExceptionCodeStd)} (0x{imports.ExceptionCodeStd:X8})\n" +
                $"  Delay-load: {PeExceptionHelper.TranslateExceptionCode(imports.ExceptionCodeDelay)} (0x{imports.ExceptionCodeDelay:X8})",
                LogMessageType.ErrorOrWarning);
        }
        else if (exceptStd)
        {
            _addLogMessage(
                $"Exception {PeExceptionHelper.TranslateExceptionCode(imports.ExceptionCodeStd)} (0x{imports.ExceptionCodeStd:X8}) occurred while processing standard imports",
                LogMessageType.ErrorOrWarning);
        }
        else if (exceptDelay)
        {
            _addLogMessage(
                $"Exception {PeExceptionHelper.TranslateExceptionCode(imports.ExceptionCodeDelay)} (0x{imports.ExceptionCodeDelay:X8}) occurred while processing delay-load imports",
                LogMessageType.ErrorOrWarning);
        }
    }

    void ProcessExports(CModule module,
                        CCoreExports rawExports,
                        List<SearchOrderType> searchOrderUM,
                        List<SearchOrderType> searchOrderKM)
    {
        foreach (var entry in rawExports.Library.Function)
        {
            module.ModuleData.Exports.Add(new(entry));
        }

        foreach (var entry in module.ParentImports)
        {
            bool bResolved = false;

            if (entry.Ordinal != UInt32.MaxValue)
            {
                bResolved = module.ModuleData.Exports?.Any(func => func.Ordinal == entry.Ordinal) == true;
            }
            else
            {
                bResolved = module.ModuleData.Exports?.Any(func => func.RawName.Equals(entry.RawName, StringComparison.Ordinal)) == true;
            }

            if (!bResolved)
            {
                module.ExportContainErrors = true;
                break;
            }
        }

    }

    void ProcessNetAssemblies(CModule module)
    {
        var dependencies = CAssemblyRefAnalyzer.GetAssemblyDependencies(module);
        CModule netDependent;
        foreach (var reference in dependencies)
        {
            if (reference.IsResolved)
            {
                netDependent = new(reference.ResolvedPath, reference.ResolvedPath, SearchOrderType.None, false);
            }
            else
            {
                netDependent = new(reference.Name, reference.Name, SearchOrderType.None, false);
            }
            netDependent.ModuleData.RuntimeVersion = module.ModuleData.RuntimeVersion;
            netDependent.ModuleData.FrameworkKind = module.ModuleData.FrameworkKind;
            netDependent.ModuleData.ResolutionSource = reference.ResolutionSource;
            netDependent.ModuleData.ReferenceVersion = reference.Version;
            netDependent.ModuleData.ReferencePublicKeyToken = reference.PublicKeyToken;
            netDependent.ModuleData.ReferenceCulture = reference.Culture;
            netDependent.IsDotNetModule = true;
            module.Dependents.Add(netDependent);
        }

        CAssemblyRefAnalyzer.ClearCache();
    }

    public void GetModuleImportExportInformation(CModule module,
                                                 List<SearchOrderType> searchOrderUM,
                                                 List<SearchOrderType> searchOrderKM,
                                                 Dictionary<int, FunctionHashObject> parentImportsHashTable,
                                                 bool EnableExperimentalFeatures)
    {
        if (module == null)
            return;
        //
        // Process exports.
        //
        CCoreExports rawExports = (CCoreExports)GetModuleInformationByType(ModuleInformationType.Exports, module);
        if (rawExports != null)
        {
            ProcessExports(module, rawExports, searchOrderUM, searchOrderKM);
        }

        //
        // Process imports.
        //
        CCoreImports rawImports = (CCoreImports)GetModuleInformationByType(ModuleInformationType.Imports, module);
        if (rawImports != null)
        {
            CheckIfKernelModule(module, rawImports);
            ProcessImports(module, false, rawImports.Library, searchOrderUM, searchOrderKM, parentImportsHashTable);
            ProcessImports(module, true, rawImports.LibraryDelay, searchOrderUM, searchOrderKM, parentImportsHashTable);
            if (rawImports.Exception != 0)
                HandleImportExceptions(rawImports);
        }

        //
        // Process .NET assembly references.
        // This feature is experimental and not production ready.
        //
        module.IsDotNetModule = module.ModuleData.ImageDotNet == 1;
        if (module.IsDotNetModule && EnableExperimentalFeatures)
        {
            ProcessNetAssemblies(module);
        }
    }

    public bool SetApiSetSchemaNamespaceUse(string fileName)
    {
        string cmd = "apisetmapsrc";

        if (!string.IsNullOrEmpty(fileName))
        {
            cmd += $" file \"{fileName}\"";
        }

        cmd += "\r\n";

        return SendRequest(cmd) && IsRequestSuccessful();
    }

    private bool GetKnownDllsByType(string command, List<string> knownDllsList, out string knownDllsPath)
    {
        if (knownDllsList == null)
        {
            knownDllsPath = string.Empty;
            return false;
        }

        CCoreKnownDlls knownDllsObject = (CCoreKnownDlls)SendCommandAndReceiveReplyAsObjectJSON(
            command, typeof(CCoreKnownDlls), null, true);
        if (knownDllsObject != null)
        {
            knownDllsList.Clear();
            if (knownDllsObject.Entries != null)
            {
                knownDllsList.AddRange(knownDllsObject.Entries);
            }
            knownDllsPath = knownDllsObject.DllPath ?? string.Empty;
            return true;
        }

        knownDllsPath = string.Empty;
        return false;
    }

    public bool GetKnownDllsAll(List<string> knownDlls, List<string> knownDlls32, out string knownDllsPath, out string knownDllsPath32)
    {
        if (knownDlls == null || knownDlls32 == null)
        {
            knownDllsPath = string.Empty;
            knownDllsPath32 = string.Empty;
            return false;
        }

        bool result32 = GetKnownDllsByType("knowndlls 32\r\n", knownDlls32, out knownDllsPath32);
        bool result64 = GetKnownDllsByType("knowndlls 64\r\n", knownDlls, out knownDllsPath);

        return result32 && result64;
    }

    private void CleanupFailedConnection()
    {
        // Safely terminate process if it's still running
        try
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                _serverProcess.Kill();
                _serverProcess.Dispose();
            }
        }
        catch { }

        _serverProcess = null;

        _dataStream?.Close();
        _dataStream = null;

        _clientConnection?.Close();
        _clientConnection = null;

        ErrorStatus = ServerErrorStatus.GeneralException;
    }

    public bool ConnectClient()
    {
        _serverProcess = null;
        string errMessage = string.Empty;

        try
        {
            string fileName = GetServerApplication();

            if (string.IsNullOrEmpty(fileName) || !File.Exists(fileName))
            {
                throw new FileNotFoundException(fileName ?? "Server application not specified");
            }

            int startAttempts = 5;
            int portNumber;
            Random rnd = new(Environment.ProcessId);

            do
            {
                portNumber = rnd.Next(CConsts.MinPortNumber, CConsts.MaxPortNumber);
                ProcessStartInfo processInfo = new()
                {
                    FileName = $"\"{fileName}\"",
                    Arguments = $"port {portNumber}",
                    UseShellExecute = false
                };

                _serverProcess = Process.Start(processInfo);

                if (_serverProcess == null)
                {
                    throw new Exception("Core process start failure");
                }

                Thread.Sleep(100);

                if (_serverProcess.HasExited)
                {
                    if (_serverProcess.ExitCode != CConsts.SERVER_ERROR_INVALIDIP)
                    {
                        throw new Exception($"Server process exited with code {_serverProcess.ExitCode}");
                    }
                }
                else
                {
                    _clientConnection = new();

                    Task connectTask = _clientConnection.ConnectAsync(IPAddress, portNumber);
                    if (Task.WaitAny(new[] { connectTask }, CORE_CONNECTION_TIMEOUT) == 0)
                    {
                        if (_clientConnection.Connected)
                        {
                            _dataStream = _clientConnection.GetStream();
                            _dataStream.ReadTimeout = CORE_NETWORK_TIMEOUT;
                            _dataStream.WriteTimeout = CORE_NETWORK_TIMEOUT;
                            Port = portNumber;
                            break;
                        }
                    }

                    _clientConnection.Dispose();
                    _clientConnection = null;
                }


            } while (--startAttempts > 0);

            // We couldn't connect server after all attempts.
            if (_clientConnection == null || !_clientConnection.Connected)
            {
                throw new Exception("Failed to connect to server after multiple attempts");
            }
        }
        catch (Exception ex)
        {
            if (ex is FileNotFoundException)
            {
                errMessage = $"{ex.Message} was not found, make sure it exist or change path to it: " +
                    $"Main menu -> Options -> Configuration, select Server tab, specify server application location and then press Connect button.";
            }
            else
            {
                errMessage = ex.Message;
            }

            CleanupFailedConnection();
            _addLogMessage($"Server failed to start: {errMessage}", LogMessageType.ErrorOrWarning);
            return false;
        }

        CBufferChain idata = ReceiveReply();
        if (idata != null)
        {
            ErrorStatus = ServerErrorStatus.NoErrors;
            _addLogMessage($"Server has been started: {new string(idata.Data)}", LogMessageType.System);
            return true;
        }
        else
        {
            ErrorStatus = ServerErrorStatus.ServerNeedRestart;
            _addLogMessage($"Server initialization failed, missing server HELLO", LogMessageType.ErrorOrWarning);
            CleanupFailedConnection();
            return false;
        }
    }

    public void DisconnectClient()
    {
        try
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                ShutdownRequest();
                Thread.Sleep(100);

                if (!_serverProcess.HasExited)
                {
                    _serverProcess.Kill();
                }
            }
        }
        catch (Exception ex)
        {
            _addLogMessage($"Error during server shutdown: {ex.Message}", LogMessageType.ErrorOrWarning);
        }
        finally
        {
            if (_dataStream != null)
            {
                _dataStream.Close();
                _dataStream = null;
            }
            if (_clientConnection != null)
            {
                _clientConnection.Close();
                _clientConnection = null;
            }

            if (_serverProcess != null)
            {
                _serverProcess.Dispose();
                _serverProcess = null;
            }
        }
    }
}
