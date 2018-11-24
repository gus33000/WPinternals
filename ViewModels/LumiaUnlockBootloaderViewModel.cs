// Copyright (c) 2018, Rene Lergner - wpinternals.net - @Heathcliff74xda
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WPinternals
{
    internal class LumiaUnlockBootloaderViewModel
    {
        internal static byte[] GetGptChunk(NokiaFlashModel FlashModel, UInt32 Size)
        {
            // This function is also used to generate a dummy chunk to flash for testing.
            // The dummy chunk will contain the GPT, so it can be flashed to the first sectors for testing.
            byte[] GPTChunk = new byte[Size];

            PhoneInfo Info = FlashModel.ReadPhoneInfo(ExtendedInfo: false);
            FlashAppType OriginalAppType = Info.App;
            bool Switch = ((Info.App != FlashAppType.BootManager) && Info.SecureFfuEnabled && !Info.Authenticated && !Info.RdcPresent);
            if (Switch)
                FlashModel.SwitchToBootManagerContext();

            byte[] Request = new byte[0x04];
            const string Header = "NOKT";

            System.Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes(Header), 0, Request, 0, Header.Length);

            byte[] Buffer = FlashModel.ExecuteRawMethod(Request);
            if ((Buffer == null) || (Buffer.Length < 0x4408))
                throw new InvalidOperationException("Unable to read GPT!");

            UInt16 Error = (UInt16)((Buffer[6] << 8) + Buffer[7]);
            if (Error > 0)
                throw new NotSupportedException("ReadGPT: Error 0x" + Error.ToString("X4"));

            System.Buffer.BlockCopy(Buffer, 8, GPTChunk, 0, 0x4400);

            if (Switch)
            {
                if (OriginalAppType == FlashAppType.FlashApp)
                    FlashModel.SwitchToFlashAppContext();
                else
                    FlashModel.SwitchToPhoneInfoAppContext();
            }

            return GPTChunk;
        }

        internal static async Task LumiaRelockUEFI(PhoneNotifierViewModel Notifier, string FFUPath = null, bool DoResetFirst = true, SetWorkingStatus SetWorkingStatus = null, UpdateWorkingStatus UpdateWorkingStatus = null, ExitSuccess ExitSuccess = null, ExitFailure ExitFailure = null)
        {
            if (SetWorkingStatus == null) SetWorkingStatus = (m, s, v, a, st) => { };
            if (UpdateWorkingStatus == null) UpdateWorkingStatus = (m, s, v, st) => { };
            if (ExitSuccess == null) ExitSuccess = (m, s) => { };
            if (ExitFailure == null) ExitFailure = (m, s) => { };

            LogFile.BeginAction("RelockPhone");
            try
            {
                GPT GPT = null;
                Partition Target = null;
                NokiaFlashModel FlashModel = null;

                LogFile.Log("Command: Relock phone", LogType.FileAndConsole);

                if (Notifier.CurrentInterface == null)
                    await Notifier.WaitForArrival();

                byte[] EFIESPBackup = null;

                PhoneInfo Info = ((NokiaFlashModel)Notifier.CurrentModel).ReadPhoneInfo();
                bool IsSpecB = Info.FlashAppProtocolVersionMajor >= 2;
                bool UndoEFIESPPadding = false;

                GPT = new GPT(GetGptChunk(((NokiaFlashModel)Notifier.CurrentModel), 0x20000));
                Partition IsUnlockedPartitionSBL3 = GPT.GetPartition("IS_UNLOCKED_SBL3");
                if (IsUnlockedPartitionSBL3 == null)
                {
                    Partition BackNV = GPT.GetPartition("BACKUP_BS_NV");
                    if (BackNV != null)
                        UndoEFIESPPadding = true;
                }
                
                if (IsSpecB || IsUnlockedPartitionSBL3 != null)
                {
                    try
                    {
                        if (Notifier.CurrentInterface != PhoneInterfaces.Lumia_MassStorage)
                        {
                            await SwitchModeViewModel.SwitchToWithStatus(Notifier, PhoneInterfaces.Lumia_MassStorage, SetWorkingStatus, UpdateWorkingStatus);
                        }

                        if (!(Notifier.CurrentModel is MassStorage))
                            throw new WPinternalsException("Failed to switch to Mass Storage mode");

                        SetWorkingStatus("Patching...", null, null, Status: WPinternalsStatus.Patching);

                        // Now relock the phone
                        MassStorage Storage = (MassStorage)Notifier.CurrentModel;

                        App.PatchEngine.TargetPath = Storage.Drive + "\\EFIESP\\";
                        App.PatchEngine.Restore("SecureBootHack-V2-EFIESP");
                        App.PatchEngine.Restore("SecureBootHack-V1.1-EFIESP");
                        App.PatchEngine.Restore("SecureBootHack-V1-EFIESP");

                        App.PatchEngine.TargetPath = Storage.Drive + "\\";
                        App.PatchEngine.Restore("SecureBootHack-MainOS");
                        App.PatchEngine.Restore("RootAccess-MainOS");

                        // Edit BCD
                        LogFile.Log("Edit BCD");
                        using (Stream BCDFileStream = new System.IO.FileStream(Storage.Drive + @"\EFIESP\efi\Microsoft\Boot\BCD", FileMode.Open, FileAccess.ReadWrite))
                        {
                            using (DiscUtils.Registry.RegistryHive BCDHive = new DiscUtils.Registry.RegistryHive(BCDFileStream))
                            {
                                DiscUtils.BootConfig.Store BCDStore = new DiscUtils.BootConfig.Store(BCDHive.Root);
                                DiscUtils.BootConfig.BcdObject MobileStartupObject = BCDStore.GetObject(new Guid("{01de5a27-8705-40db-bad6-96fa5187d4a6}"));
                                DiscUtils.BootConfig.Element NoCodeIntegrityElement = MobileStartupObject.GetElement(0x16000048);
                                if (NoCodeIntegrityElement != null)
                                    MobileStartupObject.RemoveElement(0x16000048);

                                DiscUtils.BootConfig.BcdObject WinLoadObject = BCDStore.GetObject(new Guid("{7619dcc9-fafe-11d9-b411-000476eba25f}"));
                                NoCodeIntegrityElement = WinLoadObject.GetElement(0x16000048);
                                if (NoCodeIntegrityElement != null)
                                    WinLoadObject.RemoveElement(0x16000048);
                            }
                        }

                        Partition EFIESPPartition = GPT.GetPartition("EFIESP");
                        byte[] EFIESP = Storage.ReadSectors(EFIESPPartition.FirstSector, EFIESPPartition.SizeInSectors);
                        UInt32 EfiespSizeInSectors = (UInt32)EFIESPPartition.SizeInSectors;

                        //
                        // (ByteOperations.ReadUInt32(EFIESP, 0x20) == (EfiespSizeInSectors / 2)) was originally present in this check, but it does not seem to be reliable with all cases
                        // It should be looked as why some phones have half the sector count in gpt, compared to the real partition.
                        // With that check added, the phone won't get back its original EFIESP partition, on phones like 650s.
                        // The second check should be more than enough in any case, if we find a header named MSDOS5.0 right in the middle of EFIESP,
                        // there's not many cases other than us splitting the partition in half to get this here.
                        //
                        if ((ByteOperations.ReadAsciiString(EFIESP, (UInt32)(EFIESP.Length / 2) + 3, 8)) == "MSDOS5.0")
                        {
                            EFIESPBackup = new byte[EfiespSizeInSectors * 0x200 / 2];
                            Buffer.BlockCopy(EFIESP, (Int32)EfiespSizeInSectors * 0x200 / 2, EFIESPBackup, 0, (Int32)EfiespSizeInSectors * 0x200 / 2);
                        }

                        if (ByteOperations.ReadUInt16(EFIESP, 0xE) == LumiaUnlockBootloaderViewModel.LumiaGetEFIESPadding(GPT, new FFU(FFUPath), IsSpecB))
                            UndoEFIESPPadding = true;

                        if (Storage.DoesDeviceSupportReboot())
                        {
                            SetWorkingStatus("Rebooting phone...");
                            await SwitchModeViewModel.SwitchTo(Notifier, PhoneInterfaces.Lumia_Flash);
                        }
                        else
                        {
                            LogFile.Log("The phone is currently in Mass Storage Mode", LogType.ConsoleOnly);
                            LogFile.Log("To continue the relock-sequence, the phone needs to be rebooted", LogType.ConsoleOnly);
                            LogFile.Log("Keep the phone connected to the PC", LogType.ConsoleOnly);
                            LogFile.Log("Reboot the phone manually by pressing and holding the power-button of the phone for about 10 seconds until it vibrates", LogType.ConsoleOnly);
                            LogFile.Log("The relock-sequence will resume automatically", LogType.ConsoleOnly);
                            LogFile.Log("Waiting for manual reset of the phone...", LogType.ConsoleOnly);

                            SetWorkingStatus("You need to manually reset your phone now!", "The phone is currently in Mass Storage Mode. To continue the relock-sequence, the phone needs to be rebooted. Keep the phone connected to the PC. Reboot the phone manually by pressing and holding the power-button of the phone for about 10 seconds until it vibrates. The relock-sequence will resume automatically.", null, false, WPinternalsStatus.WaitingForManualReset);

                            await Notifier.WaitForRemoval();

                            SetWorkingStatus("Rebooting phone...");

                            await Notifier.WaitForArrival();
                        }
                    }
                    catch
                    {
                        // If switching to mass storage mode failed, then we just skip that part. This might be a half unlocked phone.
                        LogFile.Log("Skipping Mass Storage mode", LogType.FileAndConsole);
                    }
                }

                // Phone can also be in normal mode if switching to Mass Storage Mode had failed.
                if (Notifier.CurrentInterface == PhoneInterfaces.Lumia_Normal)
                    await SwitchModeViewModel.SwitchToWithStatus(Notifier, PhoneInterfaces.Lumia_Flash, SetWorkingStatus, UpdateWorkingStatus);

                if ((Notifier.CurrentInterface != PhoneInterfaces.Lumia_Bootloader) && (Notifier.CurrentInterface != PhoneInterfaces.Lumia_Flash))
                    await Notifier.WaitForArrival();

                SetWorkingStatus("Flashing...", "The phone may reboot a couple of times. Just wait for it.", null, Status: WPinternalsStatus.Initializing);

                ((NokiaFlashModel)Notifier.CurrentModel).SwitchToFlashAppContext();

                List<FlashPart> FlashParts = new List<FlashPart>();

                if (UndoEFIESPPadding)
                    FlashParts = LumiaUnlockBootloaderViewModel.LumiaGenerateUndoEFIESPFlashPayload(GPT, new FFU(FFUPath), IsSpecB);

                FlashPart Part;

                FlashModel = (NokiaFlashModel)Notifier.CurrentModel;

                // Remove IS_UNLOCKED flag in GPT
                byte[] GPTChunk = GetGptChunk(FlashModel, 0x20000); // TODO: Get proper profile FFU and get ChunkSizeInBytes
                GPT = new GPT(GPTChunk);
                bool GPTChanged = false;

                Partition IsUnlockedPartition = GPT.GetPartition("IS_UNLOCKED");
                if (IsUnlockedPartition != null)
                {
                    GPT.Partitions.Remove(IsUnlockedPartition);
                    GPTChanged = true;
                }

                IsUnlockedPartitionSBL3 = GPT.GetPartition("IS_UNLOCKED_SBL3");
                if (IsUnlockedPartitionSBL3 != null)
                {
                    GPT.Partitions.Remove(IsUnlockedPartitionSBL3);
                    GPTChanged = true;
                }

                Partition EfiEspBackupPartition = GPT.GetPartition("BACKUP_EFIESP");
                if (EfiEspBackupPartition != null)
                {
                    // This must be a left over of a half unlocked bootloader
                    Partition EfiEspPartition = GPT.GetPartition("EFIESP");
                    EfiEspBackupPartition.Name = "EFIESP";
                    EfiEspBackupPartition.LastSector = EfiEspPartition.LastSector;
                    EfiEspBackupPartition.PartitionGuid = EfiEspPartition.PartitionGuid;
                    EfiEspBackupPartition.PartitionTypeGuid = EfiEspPartition.PartitionTypeGuid;
                    GPT.Partitions.Remove(EfiEspPartition);
                    GPTChanged = true;
                }

                Partition NvBackupPartition = GPT.GetPartition("BACKUP_BS_NV");
                if (NvBackupPartition != null)
                {
                    // This must be a left over of a half unlocked bootloader
                    Partition NvPartition = GPT.GetPartition("UEFI_BS_NV");
                    NvBackupPartition.Name = "UEFI_BS_NV";
                    NvBackupPartition.PartitionGuid = NvPartition.PartitionGuid;
                    NvBackupPartition.PartitionTypeGuid = NvPartition.PartitionTypeGuid;
                    GPT.Partitions.Remove(NvPartition);
                    GPTChanged = true;
                }

                if (GPTChanged)
                {
                    GPT.Rebuild();
                    Part = new FlashPart();
                    Part.StartSector = 0;
                    Part.Stream = new MemoryStream(GPTChunk);
                    FlashParts.Add(Part);
                }

                if (EFIESPBackup != null)
                {
                    Part = new FlashPart();
                    Target = GPT.GetPartition("EFIESP");
                    Part.StartSector = (UInt32)Target.FirstSector;
                    Part.Stream = new MemoryStream(EFIESPBackup);
                    FlashParts.Add(Part);
                }

                // We should only clear NV if there was no backup NV to be restored and the current NV contains the SB unlock.
                bool NvCleared = false;
                Info = ((NokiaFlashModel)Notifier.CurrentModel).ReadPhoneInfo();
                if ((NvBackupPartition == null) && !Info.UefiSecureBootEnabled)
                {
                    // ClearNV
                    Part = new FlashPart();
                    Target = GPT.GetPartition("UEFI_BS_NV");
                    Part.StartSector = (UInt32)Target.FirstSector;
                    Part.Stream = new MemoryStream(new byte[0x40000]);
                    FlashParts.Add(Part);
                    NvCleared = true;
                }

                WPinternalsStatus LastStatus = WPinternalsStatus.Undefined;
                ulong? MaxProgressValue = null;
                await LumiaUnlockBootloaderViewModel.LumiaFlashParts(Notifier, FFUPath, false, false, FlashParts, DoResetFirst, ClearFlashingStatusAtEnd: !NvCleared,
                    SetWorkingStatus: (m, s, v, a, st) =>
                    {
                        if (SetWorkingStatus != null)
                        {
                            if ((st == WPinternalsStatus.Scanning) || (st == WPinternalsStatus.WaitingForManualReset))
                                SetWorkingStatus(m, s, v, a, st);
                            else if ((LastStatus == WPinternalsStatus.Scanning) || (LastStatus == WPinternalsStatus.WaitingForManualReset) || (LastStatus == WPinternalsStatus.Undefined))
                            {
                                MaxProgressValue = v;
                                SetWorkingStatus("Flashing...", "The phone may reboot a couple of times. Just wait for it.", v, Status: WPinternalsStatus.Flashing);
                            }
                            LastStatus = st;
                        }
                    },
                    UpdateWorkingStatus: (m, s, v, st) =>
                    {
                        if (UpdateWorkingStatus != null)
                        {
                            if ((st == WPinternalsStatus.Scanning) || (st == WPinternalsStatus.WaitingForManualReset))
                                UpdateWorkingStatus(m, s, v, st);
                            else if ((LastStatus == WPinternalsStatus.Scanning) || (LastStatus == WPinternalsStatus.WaitingForManualReset))
                                SetWorkingStatus("Flashing...", "The phone may reboot a couple of times. Just wait for it.", MaxProgressValue, Status: WPinternalsStatus.Flashing);
                            else
                                UpdateWorkingStatus("Flashing...", "The phone may reboot a couple of times. Just wait for it.", v, Status: WPinternalsStatus.Flashing);
                            LastStatus = st;
                        }
                    });

                if (NvBackupPartition != null && IsSpecB)
                {
                    // An old NV backup was restored and it possibly contained the IsFlashing flag.
                    // Can't clear it immeadiately, so we need another flash.

                    SetWorkingStatus("Flashing...", "The phone may reboot a couple of times. Just wait for it.", null, Status: WPinternalsStatus.Flashing);

                    // If last flash was a normal flash, with no forced crash at the end (!NvCleared), then we have to wait for device arrival, because it could still be detected as Flash-mode from previous flash.
                    // When phone was forcably crashed, it can be in emergency mode, or still rebooting. Then also wait for device arrival.
                    // But it is also possible that it is already in bootmgr mode after being crashed (Lumia 950 / 950XL). In that case don't wait for arrival.
                    if (!NvCleared || ((Notifier.CurrentInterface != PhoneInterfaces.Lumia_Bootloader) && (Notifier.CurrentInterface != PhoneInterfaces.Lumia_Flash)))
                        await Notifier.WaitForArrival();

                    if (Notifier.CurrentInterface == PhoneInterfaces.Lumia_Bootloader)
                        ((NokiaFlashModel)Notifier.CurrentModel).SwitchToFlashAppContext();

                    if (Notifier.CurrentInterface == PhoneInterfaces.Lumia_Flash)
                    {
                        await LumiaUnlockBootloaderViewModel.LumiaFlashParts(Notifier, FFUPath, false, false, null, DoResetFirst, ClearFlashingStatusAtEnd: true, ShowProgress: false);
                    }
                }

                LogFile.Log("Phone is relocked", LogType.FileAndConsole);
                ExitSuccess("The phone is relocked", "NOTE: Make sure the phone properly boots and shuts down at least once before you unlock it again");
            }
            catch (Exception Ex)
            {
                LogFile.LogException(Ex);
                ExitFailure("Error: " + Ex.Message, null);
            }
            finally
            {
                LogFile.EndAction("RelockPhone");
            }
        }

        internal static async Task LumiaUnlockUEFI(PhoneNotifierViewModel Notifier, string ProfileFFUPath, string EDEPath, string SupportedFFUPath, SetWorkingStatus SetWorkingStatus = null, UpdateWorkingStatus UpdateWorkingStatus = null, ExitSuccess ExitSuccess = null, ExitFailure ExitFailure = null, bool ExperimentalSpecBEFIESPUnlock = false)
        {
            LogFile.BeginAction("UnlockBootloader");
            NokiaFlashModel FlashModel = (NokiaFlashModel)Notifier.CurrentModel;

            if (SetWorkingStatus == null) SetWorkingStatus = (m, s, v, a, st) => { };
            if (UpdateWorkingStatus == null) UpdateWorkingStatus = (m, s, v, st) => { };
            if (ExitSuccess == null) ExitSuccess = (m, s) => { };
            if (ExitFailure == null) ExitFailure = (m, s) => { };

            try
            {
                PhoneInfo Info = FlashModel.ReadPhoneInfo();
                bool IsSpecB = Info.FlashAppProtocolVersionMajor >= 2;
                bool ShouldApplyOldEFIESPMethod = !ExperimentalSpecBEFIESPUnlock && IsSpecB;
                bool IsBootLoaderSecure = !Info.Authenticated && !Info.RdcPresent && Info.SecureFfuEnabled;

                if (ProfileFFUPath == null)
                    throw new ArgumentNullException("Profile FFU path is missing");

                FFU ProfileFFU = new FFU(ProfileFFUPath);

                if (IsBootLoaderSecure)
                {
                    if (!Info.PlatformID.StartsWith(ProfileFFU.PlatformID, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentNullException("Profile FFU has wrong Platform ID for connected phone");
                }

                string Patch = "SecureBootHack-V1.1-EFIESP";
                if (IsSpecB)
                    Patch = "SecureBootHack-V2-EFIESP";

                FFU SupportedFFU = null;
                if (App.PatchEngine.PatchDefinitions.Where(p => p.Name == Patch).First().TargetVersions.Any(v => v.Description == ProfileFFU.GetOSVersion()))
                    SupportedFFU = ProfileFFU;
                else if (SupportedFFUPath == null)
                    throw new ArgumentNullException("Donor-FFU with supported OS version was not provided");
                else
                {
                    SupportedFFU = new FFU(SupportedFFUPath);
                    if (!App.PatchEngine.PatchDefinitions.Where(p => p.Name == Patch).First().TargetVersions.Any(v => v.Description == SupportedFFU.GetOSVersion()))
                        throw new ArgumentNullException("Donor-FFU with supported OS version was not provided");
                }

                // TODO: Check EDE file

                LogFile.Log("Assembling data for unlock", LogType.FileAndConsole);
                SetWorkingStatus("Assembling data for unlock", null, null);
                byte[] UnlockedEFIESP = ProfileFFU.GetPartition("EFIESP");

                LumiaUnlockBootloaderViewModel.LumiaPatchEFIESP(SupportedFFU, UnlockedEFIESP, IsSpecB);

                // Create backup-partition for EFIESP
                byte[] GPTChunk = GetGptChunk(FlashModel, (UInt32)ProfileFFU.ChunkSize);
                byte[] GPTChunkBackup = new byte[GPTChunk.Length];
                Buffer.BlockCopy(GPTChunk, 0, GPTChunkBackup, 0, GPTChunk.Length);
                GPT GPT = new GPT(GPTChunk);
                bool GPTChanged = false;

                bool SBL3Eng = GPT.GetPartition("IS_UNLOCKED_SBL3") != null;

                List<FlashPart> Parts = ShouldApplyOldEFIESPMethod ? new List<FlashPart>() : LumiaUnlockBootloaderViewModel.LumiaGenerateEFIESPFlashPayload(UnlockedEFIESP, GPT, ProfileFFU, IsSpecB);
                FlashPart Part;

                UInt32 OriginalEfiespSizeInSectors = (UInt32)GPT.GetPartition("EFIESP").SizeInSectors;
                UInt32 OriginalEfiespLastSector = (UInt32)GPT.GetPartition("EFIESP").LastSector;
                if (ShouldApplyOldEFIESPMethod)
                {
                    Partition BACKUP_EFIESP = GPT.GetPartition("BACKUP_EFIESP");
                    Partition EFIESP;

                    if (BACKUP_EFIESP == null)
                    {
                        BACKUP_EFIESP = GPT.GetPartition("EFIESP");
                        Guid OriginalPartitionTypeGuid = BACKUP_EFIESP.PartitionTypeGuid;
                        Guid OriginalPartitionGuid = BACKUP_EFIESP.PartitionGuid;
                        BACKUP_EFIESP.Name = "BACKUP_EFIESP";
                        BACKUP_EFIESP.LastSector = BACKUP_EFIESP.FirstSector + ((OriginalEfiespSizeInSectors) / 2) - 1; // Original is 0x10000
                        BACKUP_EFIESP.PartitionGuid = Guid.NewGuid();
                        BACKUP_EFIESP.PartitionTypeGuid = Guid.NewGuid();
                        EFIESP = new Partition();
                        EFIESP.Name = "EFIESP";
                        EFIESP.Attributes = BACKUP_EFIESP.Attributes;
                        EFIESP.PartitionGuid = OriginalPartitionGuid;
                        EFIESP.PartitionTypeGuid = OriginalPartitionTypeGuid;
                        EFIESP.FirstSector = BACKUP_EFIESP.LastSector + 1;
                        EFIESP.LastSector = EFIESP.FirstSector + ((OriginalEfiespSizeInSectors) / 2) - 1; // Original is 0x10000
                        GPT.Partitions.Add(EFIESP);
                        GPTChanged = true;
                    }
                    EFIESP = GPT.GetPartition("EFIESP");
                    if ((UInt64)UnlockedEFIESP.Length > (EFIESP.SizeInSectors * 0x200))
                    {
                        byte[] HalfEFIESP = new byte[EFIESP.SizeInSectors * 0x200];
                        Buffer.BlockCopy(UnlockedEFIESP, 0, HalfEFIESP, 0, HalfEFIESP.Length);
                        UnlockedEFIESP = HalfEFIESP;
                        ByteOperations.WriteUInt32(UnlockedEFIESP, 0x20, (UInt32)EFIESP.SizeInSectors); // Correction of partitionsize
                    }

                    Partition EFIESPPartition = GPT.GetPartition("EFIESP");
                    if (EFIESPPartition == null)
                        throw new WPinternalsException("EFIESP partition not found!");

                    if ((UInt64)UnlockedEFIESP.Length != (EFIESPPartition.SizeInSectors * 0x200))
                        throw new WPinternalsException("New EFIESP partition has wrong size. Size = 0x" + UnlockedEFIESP.Length.ToString("X8") + ". Expected size = 0x" + (EFIESPPartition.SizeInSectors * 0x200).ToString("X8"));

                    Part = new FlashPart();
                    Part.StartSector = (UInt32)EFIESPPartition.FirstSector; // GPT is prepared for 64-bit sector-offset, but flash app isn't.
                    Part.Stream = new MemoryStream(UnlockedEFIESP);
                    Parts.Add(Part);
                }

                if (IsSpecB)
                    Parts[0].ProgressText = "Flashing unlocked bootloader (part 1)...";
                else
                    Parts[0].ProgressText = "Flashing unlocked bootloader (part 2)...";
                
                // Now add NV partition
                Partition BACKUP_BS_NV = GPT.GetPartition("BACKUP_BS_NV");
                Partition UEFI_BS_NV;
                if (BACKUP_BS_NV == null)
                {
                    BACKUP_BS_NV = GPT.GetPartition("UEFI_BS_NV");
                    Guid OriginalPartitionTypeGuid = BACKUP_BS_NV.PartitionTypeGuid;
                    Guid OriginalPartitionGuid = BACKUP_BS_NV.PartitionGuid;
                    BACKUP_BS_NV.Name = "BACKUP_BS_NV";
                    BACKUP_BS_NV.PartitionGuid = Guid.NewGuid();
                    BACKUP_BS_NV.PartitionTypeGuid = Guid.NewGuid();
                    UEFI_BS_NV = new Partition();
                    UEFI_BS_NV.Name = "UEFI_BS_NV";
                    UEFI_BS_NV.Attributes = BACKUP_BS_NV.Attributes;
                    UEFI_BS_NV.PartitionGuid = OriginalPartitionGuid;
                    UEFI_BS_NV.PartitionTypeGuid = OriginalPartitionTypeGuid;
                    UEFI_BS_NV.FirstSector = BACKUP_BS_NV.LastSector + 1;
                    UEFI_BS_NV.LastSector = UEFI_BS_NV.FirstSector + BACKUP_BS_NV.LastSector - BACKUP_BS_NV.FirstSector;
                    GPT.Partitions.Add(UEFI_BS_NV);
                    GPTChanged = true;
                }
                Part = new FlashPart();
                Partition TargetPartition = GPT.GetPartition("UEFI_BS_NV");
                Part.StartSector = (UInt32)TargetPartition.FirstSector; // GPT is prepared for 64-bit sector-offset, but flash app isn't.
                string SBRes = IsSpecB ? "WPinternals.SB" : "WPinternals.SBA";
                Part.Stream = new SeekableStream(() =>
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();

                    // Magic!
                    // The SB(A) resource is a compressed version of a raw NV-variable-partition.
                    // In this partition the SecureBoot variable is disabled.
                    // It overwrites the variable in a different NV-partition than where this variable is stored usually.
                    // This normally leads to endless-loops when the NV-variables are enumerated.
                    // But the partition contains an extra hack to break out the endless loops.
                    var stream = assembly.GetManifestResourceStream(SBRes);

                    return new DecompressedStream(stream);
                });
                Parts.Add(Part);

                if (GPTChanged)
                {
                    GPT.Rebuild();
                    Part = new FlashPart();
                    Part.StartSector = 0;
                    Part.Stream = new MemoryStream(GPTChunk);
                    Parts.Add(Part);
                }

                await LumiaUnlockBootloaderViewModel.LumiaFlashParts(Notifier, ProfileFFU.Path, false, false, Parts, true, false, true, true, false, SetWorkingStatus, UpdateWorkingStatus, null, null, EDEPath);

                if (!IsSpecB)
                {
                    ((NokiaFlashModel)Notifier.CurrentModel).ResetPhone();
                    await Notifier.WaitForArrival();
                }

                if ((Notifier.CurrentInterface != PhoneInterfaces.Lumia_Bootloader) && (Notifier.CurrentInterface != PhoneInterfaces.Lumia_Flash))
                    await Notifier.WaitForArrival();

                if ((Notifier.CurrentInterface != PhoneInterfaces.Lumia_Bootloader) && (Notifier.CurrentInterface != PhoneInterfaces.Lumia_Flash))
                    throw new WPinternalsException("Error: Phone is in wrong mode");

                if (!IsSpecB && !SBL3Eng)
                {
                    ((NokiaFlashModel)Notifier.CurrentModel).ResetPhone();

                    LogFile.Log("Bootloader unlocked!", LogType.FileAndConsole);
                    ExitSuccess("Bootloader unlocked successfully!", null);

                    return;
                }

                // Not going to retry in a loop because a second attempt will result in gears due to changed BootOrder.
                // Just inform user of problem and revert.
                // User can try again after revert.
                bool IsPhoneInBadMassStorageMode = false;
                string ErrorMessage = null;
                try
                {
                    await SwitchModeViewModel.SwitchToWithStatus(Notifier, PhoneInterfaces.Lumia_MassStorage, SetWorkingStatus, UpdateWorkingStatus);
                }
                catch (WPinternalsException Ex)
                {
                    ErrorMessage = "Error: " + Ex.Message;
                    LogFile.LogException(Ex);
                }
                catch (Exception Ex)
                {
                    LogFile.LogException(Ex);
                }

                if (Notifier.CurrentInterface == PhoneInterfaces.Lumia_BadMassStorage)
                {
                    SetWorkingStatus("You need to manually reset your phone now!", "The phone is currently in Mass Storage Mode, but the driver of the PC failed to start. Unfortunately this happens sometimes. You need to manually reset the phone now. Keep the phone connected to the PC. Reboot the phone manually by pressing and holding the power-button of the phone for about 10 seconds until it vibrates. Windows Phone Internals will automatically start to revert the changes. After the phone is fully booted again, you can retry to unlock the bootloader.", null, false, WPinternalsStatus.WaitingForManualReset);
                    await Notifier.WaitForArrival(); // Should be detected in Bootmanager mode
                    if (Notifier.CurrentInterface != PhoneInterfaces.Lumia_MassStorage)
                        IsPhoneInBadMassStorageMode = true;
                }

                if (Notifier.CurrentInterface != PhoneInterfaces.Lumia_MassStorage)
                {
                    // Probably the "BootOrder" prevents to boot to MobileStartup. Mass Storage mode depends on MobileStartup.
                    // In this case Bootarm boots straight to Winload. But Winload can't handle the change of the EFIESP partition. That will cause a bootloop.

                    SetWorkingStatus("Problem detected, rolling back...", ErrorMessage);
                    await SwitchModeViewModel.SwitchTo(Notifier, PhoneInterfaces.Lumia_Flash);
                    Parts = new List<FlashPart>();

                    // Restore original GPT, which will also reference the original NV.
                    Part = new FlashPart();
                    Part.StartSector = 0;
                    Part.Stream = new MemoryStream(GPTChunkBackup);
                    Parts.Add(Part);

                    await LumiaUnlockBootloaderViewModel.LumiaFlashParts(Notifier, ProfileFFU.Path, false, false, Parts, true, false, true, true, false, SetWorkingStatus, UpdateWorkingStatus, null, null, EDEPath);

                    // An old NV backup was restored and it possibly contained the IsFlashing flag.
                    // Can't clear it immeadiately, so we need another flash.
                    if ((Notifier.CurrentInterface != PhoneInterfaces.Lumia_Bootloader) && (Notifier.CurrentInterface != PhoneInterfaces.Lumia_Flash))
                        await Notifier.WaitForArrival();

                    if ((Notifier.CurrentInterface == PhoneInterfaces.Lumia_Bootloader) || (Notifier.CurrentInterface == PhoneInterfaces.Lumia_Flash))
                    {
                        await LumiaUnlockBootloaderViewModel.LumiaFlashParts(Notifier, ProfileFFU.Path, false, false, null, true, true, true, true, false, SetWorkingStatus, UpdateWorkingStatus, null, null, EDEPath);
                    }

                    if (IsPhoneInBadMassStorageMode)
                        ExitFailure("Failed to unlock the bootloader due to misbahaving driver. Wait for phone to boot to Windows and then try again.", "The Mass Storage driver of the PC failed to start. Unfortunately this happens sometimes. After the phone is fully booted again, you can retry to unlock the bootloader.");
                    else
                        ExitFailure("Failed to unlock the bootloader", "It is not possible to unlock the bootloader straight after flashing. NOTE: Fully reboot the phone and then properly shutdown the phone, before you can try to unlock again!");

                    return;
                }

                SetWorkingStatus("Create backup partition...", null, null);

                MassStorage MassStorage = (MassStorage)Notifier.CurrentModel;
                GPTChunk = MassStorage.ReadSectors(0, 0x100);
                GPT = new GPT(GPTChunk);

                if (ShouldApplyOldEFIESPMethod)
                {
                    Partition BACKUP_EFIESP = GPT.GetPartition("BACKUP_EFIESP");
                    byte[] BackupEFIESP = MassStorage.ReadSectors(BACKUP_EFIESP.FirstSector, BACKUP_EFIESP.SizeInSectors);

                    // Copy the backed up unlocked EFIESP for future use
                    byte[] BackupUnlockedEFIESP = new byte[UnlockedEFIESP.Length];
                    Buffer.BlockCopy(BackupEFIESP, 0, BackupUnlockedEFIESP, 0, BackupEFIESP.Length);

                    LumiaUnlockBootloaderViewModel.LumiaPatchEFIESP(SupportedFFU, BackupUnlockedEFIESP, IsSpecB);

                    SetWorkingStatus("Boot optimization...", null, null);

                    App.PatchEngine.TargetPath = MassStorage.Drive + "\\";
                    App.PatchEngine.Patch("SecureBootHack-MainOS"); // Don't care about result here. Some phones do not need this.

                    LogFile.Log("The phone is currently in Mass Storage Mode", LogType.ConsoleOnly);
                    LogFile.Log("To continue the unlock-sequence, the phone needs to be rebooted", LogType.ConsoleOnly);
                    LogFile.Log("Keep the phone connected to the PC", LogType.ConsoleOnly);
                    LogFile.Log("Reboot the phone manually by pressing and holding the power-button of the phone for about 10 seconds until it vibrates", LogType.ConsoleOnly);
                    LogFile.Log("The unlock-sequence will resume automatically", LogType.ConsoleOnly);
                    LogFile.Log("Waiting for manual reset of the phone...", LogType.ConsoleOnly);

                    SetWorkingStatus("You need to manually reset your phone now!", "The phone is currently in Mass Storage Mode. To continue the unlock-sequence, the phone needs to be rebooted. Keep the phone connected to the PC. Reboot the phone manually by pressing and holding the power-button of the phone for about 10 seconds until it vibrates. The unlock-sequence will resume automatically.", null, false, WPinternalsStatus.WaitingForManualReset);

                    await Notifier.WaitForRemoval();

                    SetWorkingStatus("Rebooting phone...");

                    await Notifier.WaitForArrival();
                    if (Notifier.CurrentInterface != PhoneInterfaces.Lumia_Bootloader)
                        throw new WPinternalsException("Phone is in wrong mode");

                    ((NokiaFlashModel)Notifier.CurrentModel).SwitchToFlashAppContext();

                    // EFIESP is appended at the end of the GPT
                    // BACKUP_EFIESP is at original location in GPT
                    Partition EFIESP = GPT.GetPartition("EFIESP");
                    UInt32 OriginalEfiespFirstSector = (UInt32)BACKUP_EFIESP.FirstSector;
                    BACKUP_EFIESP.Name = "EFIESP";
                    BACKUP_EFIESP.LastSector = OriginalEfiespLastSector;
                    BACKUP_EFIESP.PartitionGuid = EFIESP.PartitionGuid;
                    BACKUP_EFIESP.PartitionTypeGuid = EFIESP.PartitionTypeGuid;
                    GPT.Partitions.Remove(EFIESP);

                    Partition IsUnlockedFlag = GPT.GetPartition("IS_UNLOCKED");
                    if (IsUnlockedFlag == null)
                    {
                        IsUnlockedFlag = new Partition();
                        IsUnlockedFlag.Name = "IS_UNLOCKED";
                        IsUnlockedFlag.Attributes = 0;
                        IsUnlockedFlag.PartitionGuid = Guid.NewGuid();
                        IsUnlockedFlag.PartitionTypeGuid = Guid.NewGuid();
                        IsUnlockedFlag.FirstSector = 0x40;
                        IsUnlockedFlag.LastSector = 0x40;
                        GPT.Partitions.Add(IsUnlockedFlag);
                    }

                    Parts = new List<FlashPart>();
                    GPT.Rebuild();
                    Part = new FlashPart();
                    Part.StartSector = 0;
                    Part.Stream = new MemoryStream(GPTChunk);
                    Part.ProgressText = "Flashing unlocked bootloader (part 2)...";
                    Parts.Add(Part);
                    Part = new FlashPart();
                    Part.StartSector = OriginalEfiespFirstSector;
                    Part.Stream = new MemoryStream(BackupUnlockedEFIESP); // We must keep the Oiriginal EFIESP, but unlocked, for many reasons
                    Parts.Add(Part);
                    Part = new FlashPart();
                    Part.StartSector = OriginalEfiespFirstSector + ((OriginalEfiespSizeInSectors) / 2);
                    Part.Stream = new MemoryStream(BackupEFIESP);
                    Parts.Add(Part);

                    await LumiaUnlockBootloaderViewModel.LumiaFlashParts(Notifier, ProfileFFU.Path, false, false, Parts, true, true, true, true, false, SetWorkingStatus, UpdateWorkingStatus, null, null, EDEPath);
                }
                else
                {
                    ulong FirstSector = GPT.GetPartition("EFIESP").FirstSector;
                    ulong SectorCount = LumiaUnlockBootloaderViewModel.LumiaGetSecondEFIESPSectorLocation(GPT, ProfileFFU, IsSpecB) - GPT.GetPartition("EFIESP").FirstSector;
                    byte[] BackupEFIESPAllocation = MassStorage.ReadSectors(FirstSector, SectorCount);

                    // The backed up buffer includes our changed header done previously to have two EFIESPs in a single partition
                    // If we want to read the original partition we need to revert our changes to the first sector.
                    UnlockedEFIESP = new byte[GPT.GetPartition("EFIESP").SizeInSectors * 0x200];
                    Buffer.BlockCopy(BackupEFIESPAllocation, 0, UnlockedEFIESP, 0, BackupEFIESPAllocation.Length);
                    ByteOperations.WriteUInt16(UnlockedEFIESP, 0xE, ByteOperations.ReadUInt16(ProfileFFU.GetPartition("EFIESP"), 0xE));

                    LogFile.Log("Unlocking backup partition", LogType.FileAndConsole);
                    SetWorkingStatus("Unlocking backup partition", null, null);

                    LumiaUnlockBootloaderViewModel.LumiaPatchEFIESP(SupportedFFU, UnlockedEFIESP, IsSpecB);

                    SetWorkingStatus("Boot optimization...", null, null);

                    App.PatchEngine.TargetPath = MassStorage.Drive + "\\";
                    App.PatchEngine.Patch("SecureBootHack-MainOS"); // Don't care about result here. Some phones do not need this.

                    if (MassStorage.DoesDeviceSupportReboot())
                    {
                        SetWorkingStatus("Rebooting phone...");
                        await SwitchModeViewModel.SwitchTo(Notifier, PhoneInterfaces.Lumia_Flash);
                    }
                    else
                    {
                        LogFile.Log("The phone is currently in Mass Storage Mode", LogType.ConsoleOnly);
                        LogFile.Log("To continue the unlock-sequence, the phone needs to be rebooted", LogType.ConsoleOnly);
                        LogFile.Log("Keep the phone connected to the PC", LogType.ConsoleOnly);
                        LogFile.Log("Reboot the phone manually by pressing and holding the power-button of the phone for about 10 seconds until it vibrates", LogType.ConsoleOnly);
                        LogFile.Log("The unlock-sequence will resume automatically", LogType.ConsoleOnly);
                        LogFile.Log("Waiting for manual reset of the phone...", LogType.ConsoleOnly);

                        SetWorkingStatus("You need to manually reset your phone now!", "The phone is currently in Mass Storage Mode. To continue the unlock-sequence, the phone needs to be rebooted. Keep the phone connected to the PC. Reboot the phone manually by pressing and holding the power-button of the phone for about 10 seconds until it vibrates. The unlock-sequence will resume automatically.", null, false, WPinternalsStatus.WaitingForManualReset);

                        await Notifier.WaitForRemoval();

                        SetWorkingStatus("Rebooting phone...");

                        await Notifier.WaitForArrival();
                        if (Notifier.CurrentInterface != PhoneInterfaces.Lumia_Bootloader)
                            throw new WPinternalsException("Phone is in wrong mode");
                    }
                    ((NokiaFlashModel)Notifier.CurrentModel).SwitchToFlashAppContext();

                    Parts = LumiaUnlockBootloaderViewModel.LumiaGenerateEFIESPFlashPayload(UnlockedEFIESP, GPT, ProfileFFU, true);
                    
                    if (IsSpecB)
                        Parts[0].ProgressText = "Flashing unlocked bootloader (part 2)...";
                    else
                        Parts[0].ProgressText = "Flashing unlocked bootloader (part 3)...";
                    
                    await LumiaUnlockBootloaderViewModel.LumiaFlashParts(Notifier, ProfileFFU.Path, false, false, Parts, true, true, true, true, false, SetWorkingStatus, UpdateWorkingStatus, null, null, EDEPath);

                    if (!IsSpecB)
                        ((NokiaFlashModel)Notifier.CurrentModel).ResetPhone();
                }

                LogFile.Log("Bootloader unlocked!", LogType.FileAndConsole);
                ExitSuccess("Bootloader unlocked successfully!", null);
            }
            catch (Exception Ex)
            {
                LogFile.LogException(Ex);
                ExitFailure(Ex.Message, Ex is WPinternalsException ? ((WPinternalsException)Ex).SubMessage : null);
            }
            LogFile.EndAction("UnlockBootloader");
        }

        // Magic!
        // This function generates a flashing payload which allows us to write a new modified EFIESP to the phone, without changing the current EFIESP.
        // This way we always have a backup copy of the original EFIESP partition, at the beginning of the partition, and our new EFIESP, at the end of the partition!
        // The new EFIESP partition is also usable instantly without going to mass storage mode and attempt a partition place swap.
        // This hack is usable on Spec A devices unlocked, engineering phones, and Spec B phones with the Custom flash exploit.
        // Sector alignment and data length are ensured for the Custom flash exploit
        // This hack doesn't require us to modify the GPT of the device at all, the new EFIESP is written around the middle of the old one,
        // while keeping the first half of the partition intact, except the very first chunk
        internal static List<FlashPart> LumiaGenerateEFIESPFlashPayload(byte[] NewEFIESP, GPT DeviceGPT, FFU DeviceFFU, bool IsSpecB)
        {
            uint SectorSize = 512;

            Partition EFIESP = DeviceGPT.GetPartition("EFIESP");
            UInt16 ReservedOGSectors = ByteOperations.ReadUInt16(DeviceFFU.GetPartition("EFIESP"), 0xE);

            UInt64 numberofsectors = EFIESP.SizeInSectors;
            UInt64 halfnumberofsectors = numberofsectors / 2;
            UInt64 allocatednumberofsectors = halfnumberofsectors - ReservedOGSectors + 1;

            UInt16 ReservedSectors = 0xFFFF;

            if (allocatednumberofsectors < ReservedSectors)
            {
                UInt64 totalnumberofadditionalsectors = ReservedSectors - allocatednumberofsectors;
                ReservedSectors -= (ushort)totalnumberofadditionalsectors;
            }

            if (IsSpecB && (ReservedSectors % (DeviceFFU.ChunkSize / SectorSize) != 0))
            {
                ReservedSectors -= (ushort)(ReservedSectors % (DeviceFFU.ChunkSize / SectorSize));
            }

            Int32 EFIESPFirstPartSize = (int)SectorSize * ReservedOGSectors;
            if (IsSpecB && (EFIESPFirstPartSize % DeviceFFU.ChunkSize != 0))
            {
                EFIESPFirstPartSize = DeviceFFU.ChunkSize;
            }

            UInt64 FirstEFIESPSector = EFIESP.FirstSector;

            byte[] FirstSector = DeviceFFU.GetPartition("EFIESP").Take(EFIESPFirstPartSize).ToArray();
            ByteOperations.WriteUInt16(FirstSector, 0xE, ReservedSectors);

            byte[] SecondEFIESP = NewEFIESP.Skip((int)SectorSize * ReservedOGSectors).Take((int)(NewEFIESP.Length - ReservedSectors * SectorSize)).ToArray();

            List<FlashPart> Parts = new List<FlashPart>();

            FlashPart Part = new FlashPart();
            Part.StartSector = (uint)FirstEFIESPSector;
            Part.Stream = new MemoryStream(FirstSector);
            Parts.Add(Part);

            Part = new FlashPart();
            Part.StartSector = (uint)(FirstEFIESPSector + ReservedSectors);
            Part.Stream = new MemoryStream(SecondEFIESP);
            Parts.Add(Part);

            return Parts;
        }

        // Magic!
        // This function generates a flashing payload which allows us to get back the original device EFIESP without ever going to mass storage mode.
        internal static List<FlashPart> LumiaGenerateUndoEFIESPFlashPayload(GPT DeviceGPT, FFU DeviceFFU, bool IsSpecB)
        {
            uint SectorSize = 512;

            Partition EFIESP = DeviceGPT.GetPartition("EFIESP");
            UInt16 ReservedOGSectors = ByteOperations.ReadUInt16(DeviceFFU.GetPartition("EFIESP"), 0xE);

            UInt64 numberofsectors = EFIESP.SizeInSectors;
            UInt64 halfnumberofsectors = numberofsectors / 2;
            UInt64 allocatednumberofsectors = halfnumberofsectors - ReservedOGSectors + 1;

            byte[] NewEFIESP = new byte[numberofsectors * SectorSize];

            UInt16 ReservedSectors = 0xFFFF;

            if (allocatednumberofsectors < ReservedSectors)
            {
                UInt64 totalnumberofadditionalsectors = ReservedSectors - allocatednumberofsectors;
                ReservedSectors -= (ushort)totalnumberofadditionalsectors;
            }

            if (IsSpecB && (ReservedSectors % (DeviceFFU.ChunkSize / SectorSize) != 0))
            {
                ReservedSectors -= (ushort)(ReservedSectors % (DeviceFFU.ChunkSize / SectorSize));
            }

            Int32 EFIESPFirstPartSize = (int)SectorSize * ReservedOGSectors;
            if (IsSpecB && (EFIESPFirstPartSize % DeviceFFU.ChunkSize != 0))
            {
                EFIESPFirstPartSize = DeviceFFU.ChunkSize;
                LogFile.Log(DeviceFFU.ChunkSize.ToString());
            }

            UInt64 FirstEFIESPSector = EFIESP.FirstSector;

            byte[] FirstSector = DeviceFFU.GetPartition("EFIESP").Take(EFIESPFirstPartSize).ToArray();

            byte[] SecondEFIESP = NewEFIESP.Skip((int)SectorSize * ReservedOGSectors).Take((int)(NewEFIESP.Length - ReservedSectors * SectorSize)).ToArray();

            List<FlashPart> Parts = new List<FlashPart>();

            FlashPart Part = new FlashPart();
            Part.StartSector = (uint)FirstEFIESPSector;
            Part.Stream = new MemoryStream(FirstSector);
            Parts.Add(Part);

            Part = new FlashPart();
            Part.StartSector = (uint)(FirstEFIESPSector + ReservedSectors);
            Part.Stream = new MemoryStream(SecondEFIESP);
            Parts.Add(Part);

            return Parts;
        }

        // Magic!
        // This function gets the first sector of the new EFIESP location without ever going to mass storage mode.
        internal static UInt64 LumiaGetSecondEFIESPSectorLocation(GPT DeviceGPT, FFU DeviceFFU, bool IsSpecB)
        {
            uint SectorSize = 512;
            Partition EFIESP = DeviceGPT.GetPartition("EFIESP");
            UInt16 ReservedOGSectors = ByteOperations.ReadUInt16(DeviceFFU.GetPartition("EFIESP"), 0xE);

            UInt64 numberofsectors = EFIESP.SizeInSectors;

            UInt64 halfnumberofsectors = numberofsectors / 2;
            UInt64 allocatednumberofsectors = halfnumberofsectors - ReservedOGSectors + 1;

            UInt16 ReservedSectors = 65535;

            if (allocatednumberofsectors < ReservedSectors)
            {
                UInt64 totalnumberofadditionalsectors = ReservedSectors - allocatednumberofsectors;
                ReservedSectors -= (ushort)totalnumberofadditionalsectors;
            }

            if (IsSpecB && (ReservedSectors % (DeviceFFU.ChunkSize / SectorSize) != 0))
            {
                ReservedSectors -= (ushort)(ReservedSectors % (DeviceFFU.ChunkSize / SectorSize));
            }

            return EFIESP.FirstSector + ReservedSectors;
        }

        // Magic!
        // This function gets the padding new EFIESP location without ever going to mass storage mode.
        internal static UInt64 LumiaGetEFIESPadding(GPT DeviceGPT, FFU DeviceFFU, bool IsSpecB)
        {
            uint SectorSize = 512;
            Partition EFIESP = DeviceGPT.GetPartition("EFIESP");
            UInt16 ReservedOGSectors = ByteOperations.ReadUInt16(DeviceFFU.GetPartition("EFIESP"), 0xE);

            UInt64 numberofsectors = EFIESP.SizeInSectors;

            UInt64 halfnumberofsectors = numberofsectors / 2;
            UInt64 allocatednumberofsectors = halfnumberofsectors - ReservedOGSectors + 1;

            UInt16 ReservedSectors = 65535;

            if (allocatednumberofsectors < ReservedSectors)
            {
                UInt64 totalnumberofadditionalsectors = ReservedSectors - allocatednumberofsectors;
                ReservedSectors -= (ushort)totalnumberofadditionalsectors;
            }

            if (IsSpecB && (ReservedSectors % (DeviceFFU.ChunkSize / SectorSize) != 0))
            {
                ReservedSectors -= (ushort)(ReservedSectors % (DeviceFFU.ChunkSize / SectorSize));
            }

            return ReservedSectors;
        }
        
        internal static async Task LumiaFlashParts(PhoneNotifierViewModel Notifier, string FFUPath, bool PerformFullFlashFirst, bool SkipWrite, List<FlashPart> Parts, bool DoResetFirst = true, bool ClearFlashingStatusAtEnd = true, bool CheckSectorAlignment = true, bool ShowProgress = true, bool Experimental = false, SetWorkingStatus SetWorkingStatus = null, UpdateWorkingStatus UpdateWorkingStatus = null, ExitSuccess ExitSuccess = null, ExitFailure ExitFailure = null, string EDEPath = null)
        {
            PhoneInfo Info = ((NokiaFlashModel)Notifier.CurrentModel).ReadPhoneInfo();
            bool IsSpecA = Info.FlashAppProtocolVersionMajor < 2;

            if (IsSpecA)
                LumiaV1FlashParts(Notifier, Parts, SetWorkingStatus, UpdateWorkingStatus);
            else
                await LumiaV2UnlockBootViewModel.LumiaV2CustomFlash(Notifier, FFUPath, PerformFullFlashFirst, SkipWrite, Parts, DoResetFirst, ClearFlashingStatusAtEnd, CheckSectorAlignment, ShowProgress, Experimental, SetWorkingStatus, UpdateWorkingStatus, ExitSuccess, ExitFailure, EDEPath);
        }

        private static void LumiaV1FlashParts(PhoneNotifierViewModel Notifier, List<FlashPart> FlashParts, SetWorkingStatus SetWorkingStatus = null, UpdateWorkingStatus UpdateWorkingStatus = null)
        {
            SetWorkingStatus("Initializing flash...", null, 100, Status: WPinternalsStatus.Initializing);

            NokiaFlashModel FlashModel = (NokiaFlashModel)Notifier.CurrentModel;

            UInt64 InputStreamLength = 0;
            UInt64 totalwritten = 0;
            int ProgressPercentage = 0;

            foreach (FlashPart Part in FlashParts)
                InputStreamLength += (ulong)Part.Stream.Length;

            foreach (FlashPart Part in FlashParts)
            {
                Stream InputStream = new DecompressedStream(Part.Stream);

                if (InputStream != null)
                {
                    using (InputStream)
                    {
                        const int FlashBufferSize = 0x200000; // Flash 8 GB phone -> buffersize 0x200000 = 11:45 min, buffersize 0x20000 = 12:30 min
                        byte[] FlashBuffer = new byte[FlashBufferSize];
                        int BytesRead;
                        UInt64 i = 0;
                        do
                        {
                            BytesRead = InputStream.Read(FlashBuffer, 0, FlashBufferSize);

                            byte[] FlashBufferFinalSize;
                            if (BytesRead > 0)
                            {
                                if (BytesRead == FlashBufferSize)
                                    FlashBufferFinalSize = FlashBuffer;
                                else
                                {
                                    FlashBufferFinalSize = new byte[BytesRead];
                                    Buffer.BlockCopy(FlashBuffer, 0, FlashBufferFinalSize, 0, BytesRead);
                                }

                                FlashModel.FlashSectors((UInt32)(Part.StartSector + (i / 0x200)), FlashBufferFinalSize, ProgressPercentage);
                            }

                            UpdateWorkingStatus(Part.ProgressText, null, (uint)ProgressPercentage, WPinternalsStatus.Flashing);
                            totalwritten += (UInt64)FlashBuffer.Length / 0x200;
                            ProgressPercentage = (int)((double)totalwritten / (UInt64)(InputStreamLength / 0x200) * 100);

                            i += FlashBufferSize;
                        }
                        while (BytesRead == FlashBufferSize);
                    }
                }
            }

            UpdateWorkingStatus(null, null, 100, WPinternalsStatus.Flashing);
        }

        internal static void LumiaPatchEFIESP(FFU SupportedFFU, byte[] EFIESPPartition, bool SpecB)
        {
            using (DiscUtils.Fat.FatFileSystem EFIESPFileSystem = new DiscUtils.Fat.FatFileSystem(new MemoryStream(EFIESPPartition)))
            {
                App.PatchEngine.TargetImage = EFIESPFileSystem;

                string PatchDefinition = "SecureBootHack-V1.1-EFIESP";
                if (SpecB)
                    PatchDefinition = "SecureBootHack-V2-EFIESP";

                bool PatchResult = App.PatchEngine.Patch(PatchDefinition);
                if (!PatchResult)
                {
                    LogFile.Log("Donor-FFU: " + SupportedFFU.Path);
                    byte[] SupportedEFIESP = SupportedFFU.GetPartition("EFIESP");

                    using (DiscUtils.Fat.FatFileSystem SupportedEFIESPFileSystem = new DiscUtils.Fat.FatFileSystem(new MemoryStream(SupportedEFIESP)))
                    using (DiscUtils.SparseStream SupportedMobileStartupStream = SupportedEFIESPFileSystem.OpenFile(@"\Windows\System32\Boot\mobilestartup.efi", FileMode.Open))
                    using (MemoryStream SupportedMobileStartupMemStream = new MemoryStream())
                    using (Stream MobileStartupStream = EFIESPFileSystem.OpenFile(@"Windows\System32\Boot\mobilestartup.efi", FileMode.Create, FileAccess.Write))
                    {
                        SupportedMobileStartupStream.CopyTo(SupportedMobileStartupMemStream);
                        byte[] SupportedMobileStartup = SupportedMobileStartupMemStream.ToArray();

                        // Save supported mobilestartup.efi
                        LogFile.Log("Taking mobilestartup.efi from donor-FFU");
                        MobileStartupStream.Write(SupportedMobileStartup, 0, SupportedMobileStartup.Length);
                    }

                    PatchResult = App.PatchEngine.Patch(PatchDefinition);
                    if (!PatchResult)
                        throw new WPinternalsException("Failed to patch bootloader");
                }

                LogFile.Log("Edit BCD");
                using (Stream BCDFileStream = EFIESPFileSystem.OpenFile(@"efi\Microsoft\Boot\BCD", FileMode.Open, FileAccess.ReadWrite))
                {
                    using (DiscUtils.Registry.RegistryHive BCDHive = new DiscUtils.Registry.RegistryHive(BCDFileStream))
                    {
                        DiscUtils.BootConfig.Store BCDStore = new DiscUtils.BootConfig.Store(BCDHive.Root);
                        DiscUtils.BootConfig.BcdObject MobileStartupObject = BCDStore.GetObject(new Guid("{01de5a27-8705-40db-bad6-96fa5187d4a6}"));
                        DiscUtils.BootConfig.Element NoCodeIntegrityElement = MobileStartupObject.GetElement(0x16000048);
                        if (NoCodeIntegrityElement != null)
                            NoCodeIntegrityElement.Value = DiscUtils.BootConfig.ElementValue.ForBoolean(true);
                        else
                            MobileStartupObject.AddElement(0x16000048, DiscUtils.BootConfig.ElementValue.ForBoolean(true));

                        DiscUtils.BootConfig.BcdObject WinLoadObject = BCDStore.GetObject(new Guid("{7619dcc9-fafe-11d9-b411-000476eba25f}"));
                        NoCodeIntegrityElement = WinLoadObject.GetElement(0x16000048);
                        if (NoCodeIntegrityElement != null)
                            NoCodeIntegrityElement.Value = DiscUtils.BootConfig.ElementValue.ForBoolean(true);
                        else
                            WinLoadObject.AddElement(0x16000048, DiscUtils.BootConfig.ElementValue.ForBoolean(true));
                    }
                }
            }
        }
    }
}