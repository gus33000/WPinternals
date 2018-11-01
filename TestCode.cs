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
using System.IO;
using System.Threading.Tasks;

namespace WPinternals
{
    internal static class TestCode
    {
        internal static async Task Test(System.Threading.SynchronizationContext UIContext)
        {
            // To avoid warnings when there is no code here.
            await Task.Run(() => { });

            // PhoneNotifierViewModel Notifier = new PhoneNotifierViewModel();
            // Notifier.Start();
            // await SwitchModeViewModel.SwitchTo(Notifier, PhoneInterfaces.Lumia_MassStorage);
            // MassStorage MassStorage = (MassStorage)Notifier.CurrentModel;
        }

        internal static async Task EnableBootPolicyChecks(System.Threading.SynchronizationContext UIContext)
        {
            LogFile.Log("Command: Enable Boot Policy Checks", LogType.FileAndConsole);

            PhoneNotifierViewModel Notifier = new PhoneNotifierViewModel();
            Notifier.Start();
            await SwitchModeViewModel.SwitchTo(Notifier, PhoneInterfaces.Lumia_Flash);

            NokiaFlashModel Flash = (NokiaFlashModel)Notifier.CurrentModel;

            // Use GetGptChunk() here instead of ReadGPT(), because ReadGPT() skips the first sector.
            // We need the fist sector if we want to write back the GPT.
            byte[] GPTChunk = LumiaV2UnlockBootViewModel.GetGptChunk(Flash, 0x20000);
            GPT GPT = new GPT(GPTChunk);
            bool GPTChanged = false;
            Partition NvBackupPartition = GPT.GetPartition("BACKUP_BS_NV");
            if (NvBackupPartition != null)
            {
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
                Flash.FlashSectors(0, GPTChunk, 0);
            }
            
            await SwitchModeViewModel.SwitchTo(Notifier, PhoneInterfaces.Lumia_MassStorage);
            MassStorage MassStorage = (MassStorage)Notifier.CurrentModel;

            LogFile.Log("Patching bootarm.efi", LogType.FileAndConsole);
            string bootarm = MassStorage.Drive + @"\EFIESP\efi\boot\bootarm.efi";
            byte[] bootarmbuffer = File.ReadAllBytes(bootarm);

            uint? secoffset = ByteOperations.FindUnicode(bootarmbuffer, "BecureBoot");

            if (secoffset.HasValue)
                ByteOperations.WriteUnicodeString(bootarmbuffer, secoffset.Value, "S");

            CalculateChecksum(bootarmbuffer);

            File.WriteAllBytes(bootarm, bootarmbuffer);

            LogFile.Log("Patching mobilestartup.efi", LogType.FileAndConsole);

            string ms = MassStorage.Drive + @"\EFIESP\Windows\System32\Boot\mobilestartup.efi";

            byte[] msbuffer = File.ReadAllBytes(ms);
            
            uint? curoffset = ByteOperations.FindUnicode(msbuffer, "BurrentPolicy");
            
            if (curoffset.HasValue)
                ByteOperations.WriteUnicodeString(msbuffer, curoffset.Value, "C");

            CalculateChecksum(msbuffer);

            File.WriteAllBytes(ms, msbuffer);

            LogFile.Log("Patching winload.efi", LogType.FileAndConsole);

            string winload = MassStorage.Drive + @"\Windows\System32\Boot\winload.efi";

            byte[] wlbuffer = File.ReadAllBytes(winload);

            secoffset = ByteOperations.FindUnicode(wlbuffer, "BecureBoot");

            if (secoffset.HasValue)
                ByteOperations.WriteUnicodeString(wlbuffer, secoffset.Value, "S");

            CalculateChecksum(wlbuffer);

            File.WriteAllBytes(winload, wlbuffer);

            Notifier.Stop();

            LogFile.Log("Boot Policy Checks re-enabled successfully!", LogType.FileAndConsole);
        }

        internal static async Task DisableBootPolicyChecks(System.Threading.SynchronizationContext UIContext)
        {
            LogFile.Log("Command: Disable Boot Policy Checks", LogType.FileAndConsole);

            PhoneNotifierViewModel Notifier = new PhoneNotifierViewModel();
            Notifier.Start();
            await SwitchModeViewModel.SwitchTo(Notifier, PhoneInterfaces.Lumia_Flash);

            NokiaFlashModel Flash = (NokiaFlashModel)Notifier.CurrentModel;

            // Use GetGptChunk() here instead of ReadGPT(), because ReadGPT() skips the first sector.
            // We need the fist sector if we want to write back the GPT.
            byte[] GPTChunk = LumiaV2UnlockBootViewModel.GetGptChunk(Flash, 0x20000);
            GPT GPT = new GPT(GPTChunk);
            bool GPTChanged = false;
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
            if (GPTChanged)
            {
                GPT.Rebuild();
                Flash.FlashSectors(0, GPTChunk, 0);
            }

            byte[] buff = new byte[0x40000];

            Partition Target = GPT.GetPartition("UEFI_BS_NV");
            Flash.FlashSectors((uint)Target.FirstSector, buff);

            await SwitchModeViewModel.SwitchTo(Notifier, PhoneInterfaces.Lumia_MassStorage);
            MassStorage MassStorage = (MassStorage)Notifier.CurrentModel;
            
            App.PatchEngine.TargetPath = MassStorage.Drive + @"\EFIESP\";

            LogFile.Log("Enabling Root Access on EFIESP", LogType.FileAndConsole);
            bool Result = App.PatchEngine.Patch("SecureBootHack-V1-EFIESP");
            if (!Result)
            {
                LogFile.Log("Unable to disable boot policies on EFIESP!", LogType.FileAndConsole);
                return;
            }


            App.PatchEngine.TargetPath = MassStorage.Drive + @"\";

            LogFile.Log("Enabling Root Access on MainOS", LogType.FileAndConsole);
            Result = App.PatchEngine.Patch("SecureBootHack-MainOS");
            if (!Result)
            {
                LogFile.Log("Unable to disable boot policies on MainOS!", LogType.FileAndConsole);
                return;
            }

            LogFile.Log("Patching bootarm.efi", LogType.FileAndConsole);
            string bootarm = MassStorage.Drive + @"\EFIESP\efi\boot\bootarm.efi";

            byte[] bootarmbuffer = File.ReadAllBytes(bootarm);

            uint? secoffset = ByteOperations.FindUnicode(bootarmbuffer, "\0SecureBoot");

            if (secoffset.HasValue)
                ByteOperations.WriteUnicodeString(bootarmbuffer, secoffset.Value, "\0B");


            CalculateChecksum(bootarmbuffer);

            File.WriteAllBytes(bootarm, bootarmbuffer);

            LogFile.Log("Patching mobilestartup.efi", LogType.FileAndConsole);
            
            string ms = MassStorage.Drive + @"\EFIESP\Windows\System32\Boot\mobilestartup.efi";

            byte[] msbuffer = File.ReadAllBytes(ms);
            
            uint? curoffset = ByteOperations.FindUnicode(msbuffer, "CurrentPolicy");
            
            if (curoffset.HasValue)
                ByteOperations.WriteUnicodeString(msbuffer, curoffset.Value, "B");

            CalculateChecksum(msbuffer);

            File.WriteAllBytes(ms, msbuffer);
            
            LogFile.Log("Patching winload.efi", LogType.FileAndConsole);

            string winload = MassStorage.Drive + @"\Windows\System32\Boot\winload.efi";

            byte[] wlbuffer = File.ReadAllBytes(winload);

            secoffset = ByteOperations.FindUnicode(wlbuffer, "SecureBoot");

            if (secoffset.HasValue)
                ByteOperations.WriteUnicodeString(wlbuffer, secoffset.Value, "B");

            CalculateChecksum(wlbuffer);

            File.WriteAllBytes(winload, wlbuffer);

            Notifier.Stop();

            LogFile.Log("Boot Policy Checks disabled successfully!", LogType.FileAndConsole);
        }

        private static UInt32 CalculateChecksum(byte[] PEFile)
        {
            UInt32 Checksum = 0;
            UInt32 Hi;

            // Clear file checksum
            ByteOperations.WriteUInt32(PEFile, GetChecksumOffset(PEFile), 0);

            for (UInt32 i = 0; i < ((UInt32)PEFile.Length & 0xfffffffe); i += 2)
            {
                Checksum += ByteOperations.ReadUInt16(PEFile, i);
                Hi = Checksum >> 16;
                if (Hi != 0)
                {
                    Checksum = Hi + (Checksum & 0xFFFF);
                }
            }
            if ((PEFile.Length % 2) != 0)
            {
                Checksum += (UInt32)ByteOperations.ReadUInt8(PEFile, (UInt32)PEFile.Length - 1);
                Hi = Checksum >> 16;
                if (Hi != 0)
                {
                    Checksum = Hi + (Checksum & 0xFFFF);
                }
            }
            Checksum += (UInt32)PEFile.Length;

            // Write file checksum
            ByteOperations.WriteUInt32(PEFile, GetChecksumOffset(PEFile), Checksum);

            return Checksum;
        }

        private static UInt32 GetChecksumOffset(byte[] PEFile)
        {
            return ByteOperations.ReadUInt32(PEFile, 0x3C) + +0x58;
        }

        internal static async Task TestProgrammer(System.Threading.SynchronizationContext UIContext, string ProgrammerPath)
        {
            LogFile.BeginAction("TestProgrammer");
            try
            {
                LogFile.Log("Starting Firehose Test", LogType.FileAndConsole);

                PhoneNotifierViewModel Notifier = new PhoneNotifierViewModel();
                UIContext.Send(s => Notifier.Start(), null);
                if (Notifier.CurrentInterface == PhoneInterfaces.Qualcomm_Download)
                    LogFile.Log("Phone found in emergency mode", LogType.FileAndConsole);
                else
                {
                    LogFile.Log("Phone needs to be switched to emergency mode.", LogType.FileAndConsole);
                    await SwitchModeViewModel.SwitchTo(Notifier, PhoneInterfaces.Lumia_Flash);
                    PhoneInfo Info = ((NokiaFlashModel)Notifier.CurrentModel).ReadPhoneInfo();
                    Info.Log(LogType.ConsoleOnly);
                    await SwitchModeViewModel.SwitchTo(Notifier, PhoneInterfaces.Qualcomm_Download);
                    if (Notifier.CurrentInterface != PhoneInterfaces.Qualcomm_Download)
                        throw new WPinternalsException("Switching mode failed.");
                    LogFile.Log("Phone is in emergency mode.", LogType.FileAndConsole);
                }

                // Send and start programmer
                QualcommSerial Serial = (QualcommSerial)Notifier.CurrentModel;
                QualcommSahara Sahara = new QualcommSahara(Serial);

                if (await Sahara.Reset(ProgrammerPath))
                    LogFile.Log("Emergency programmer test succeeded", LogType.FileAndConsole);
                else
                    LogFile.Log("Emergency programmer test failed", LogType.FileAndConsole);
            }
            catch (Exception Ex)
            {
                LogFile.LogException(Ex);
            }
            finally
            {
                LogFile.EndAction("TestProgrammer");
            }
        }
    }
}
