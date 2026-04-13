import { useState, useCallback } from "react";
import { invoke } from "@tauri-apps/api/core";
import { open as openDialog } from "@tauri-apps/plugin-dialog";
import { listen } from "@tauri-apps/api/event";
import { Home } from "@/components/Home";
import { VolumeInfoPanel } from "@/components/VolumeInfoPanel";
import { FileBrowser } from "@/components/FileBrowser";
import { MountPanel } from "@/components/MountPanel";
import { Toaster } from "@/components/ui/sonner";
import { toast } from "sonner";

export type AppStep = "idle" | "file_selected" | "unlocking" | "unlocked" | "mounting" | "mounted";

export interface VolumeInfo {
  encryption: string;
  hash: string;
  header_version: number;
  volume_size: number;
  encrypted_area_start: number;
  encrypted_area_length: number;
  sector_size: number;
  is_hidden: boolean;
}

export interface FileEntry {
  name: string;
  path: string;
  size: number;
  is_dir: boolean;
}

function App() {
  const [step, setStep] = useState<AppStep>("idle");
  const [filePath, setFilePath] = useState<string>("");
  const [volumeInfo, setVolumeInfo] = useState<VolumeInfo | null>(null);
  const [fsType, setFsType] = useState<string>("");
  const [files, setFiles] = useState<FileEntry[]>([]);
  const [driveLetter, setDriveLetter] = useState<string | null>(null);
  const [extractProgress, setExtractProgress] = useState<number>(0);
  const [isExtracting, setIsExtracting] = useState(false);
  const [isMounting, setIsMounting] = useState(false);
  const [isUnmounting, setIsUnmounting] = useState(false);
  const [mountProgress, setMountProgress] = useState<number>(0);
  const [mountStage, setMountStage] = useState<string>("");

  const handleSelectFile = useCallback(async () => {
    const selected = await openDialog({
      multiple: false,
      filters: [
        { name: "TrueCrypt Volume", extensions: ["tc", "hc"] },
        { name: "All Files", extensions: ["*"] },
      ],
    });
    if (selected) {
      setFilePath(selected as string);
      setStep("file_selected");
    }
  }, []);

  const handleFileDrop = useCallback((path: string) => {
    setFilePath(path);
    setStep("file_selected");
  }, []);

  const handleUnlock = useCallback(
    async (password: string) => {
      setStep("unlocking");
      try {
        const result = await invoke<{ info: VolumeInfo; fs_type: string }>(
          "open_volume",
          { path: filePath, password }
        );
        setVolumeInfo(result.info);
        setFsType(result.fs_type);

        // Load file listing
        try {
          const fileList = await invoke<FileEntry[]>("list_files", {
            path: "\\",
          });
          setFiles(fileList);
        } catch (e) {
          console.warn("Could not list files:", e);
          setFiles([]);
        }

        setStep("unlocked");
      } catch (e) {
        setStep("file_selected");
        toast.error("Failed to unlock volume", {
          description: String(e),
        });
      }
    },
    [filePath]
  );

  const handleExtractAll = useCallback(async () => {
    const dest = await openDialog({
      directory: true,
      title: "Select extraction destination",
    });
    if (!dest) return;

    setIsExtracting(true);
    setExtractProgress(0);

    const unlisten = await listen<number>("extract-progress", (event) => {
      setExtractProgress(event.payload);
    });

    try {
      const count = await invoke<number>("extract_all", { dest });
      toast.success(`Extracted ${count} files successfully`);
    } catch (e) {
      toast.error("Extraction failed", { description: String(e) });
    } finally {
      unlisten();
      setIsExtracting(false);
      setExtractProgress(0);
    }
  }, []);

  const handleExtractSelected = useCallback(async (paths: string[]) => {
    const dest = await openDialog({
      directory: true,
      title: "Select extraction destination",
    });
    if (!dest) return;

    setIsExtracting(true);
    setExtractProgress(0);

    const unlisten = await listen<number>("extract-progress", (event) => {
      setExtractProgress(event.payload);
    });

    try {
      const count = await invoke<number>("extract_files", {
        dest,
        paths,
      });
      toast.success(`Extracted ${count} files successfully`);
    } catch (e) {
      toast.error("Extraction failed", { description: String(e) });
    } finally {
      unlisten();
      setIsExtracting(false);
      setExtractProgress(0);
    }
  }, []);

  const handleMount = useCallback(async () => {
    setIsMounting(true);
    setMountProgress(0);
    setMountStage("");

    const unlisten = await listen<{ stage: string; progress: number }>(
      "mount-progress",
      (event) => {
        setMountStage(event.payload.stage);
        setMountProgress(event.payload.progress);
      }
    );

    try {
      const result = await invoke<{ drive_letter: string | null }>(
        "mount_volume"
      );
      setDriveLetter(result.drive_letter);
      setStep("mounted");
      if (result.drive_letter) {
        toast.success(`Volume mounted as ${result.drive_letter}`);
      } else {
        toast.info(
          "VHD mounted but no drive letter assigned. Check Disk Management."
        );
      }
    } catch (e) {
      toast.error("Mount failed", { description: String(e) });
    } finally {
      unlisten();
      setIsMounting(false);
      setMountProgress(0);
      setMountStage("");
    }
  }, []);

  const handleUnmount = useCallback(async () => {
    setIsUnmounting(true);

    try {
      await invoke("unmount_volume");
      setDriveLetter(null);
      setStep("unlocked");
      toast.success("Volume unmounted and changes saved");
    } catch (e) {
      toast.error("Unmount failed", { description: String(e) });
    } finally {
      setIsUnmounting(false);
    }
  }, []);

  const handleClose = useCallback(async () => {
    try {
      await invoke("close_volume");
    } catch {
      // Ignore errors on close
    }
    setStep("idle");
    setFilePath("");
    setVolumeInfo(null);
    setFsType("");
    setFiles([]);
    setDriveLetter(null);
    setIsMounting(false);
    setIsUnmounting(false);
    setMountProgress(0);
    setMountStage("");
  }, []);

  return (
    <div className="min-h-screen bg-background">
      <div className="border-b px-6 py-3 flex items-center justify-between">
        <h1 className="text-lg font-semibold text-foreground">TC Drive</h1>
        {step !== "idle" && (
          <button
            onClick={handleClose}
            className="text-sm text-muted-foreground hover:text-foreground transition-colors"
          >
            Close Volume
          </button>
        )}
      </div>

      <div className="mx-auto max-w-4xl p-6 space-y-4">
        {(step === "idle" || step === "file_selected") && (
          <Home
            filePath={filePath}
            onSelectFile={handleSelectFile}
            onFileDrop={handleFileDrop}
            onUnlock={step === "file_selected" ? handleUnlock : undefined}
          />
        )}

        {step === "unlocking" && (
          <div className="flex items-center justify-center py-20">
            <div className="text-center space-y-3">
              <div className="animate-spin h-8 w-8 border-2 border-primary border-t-transparent rounded-full mx-auto" />
              <p className="text-muted-foreground">Decrypting volume header...</p>
              <p className="text-xs text-muted-foreground">
                Trying all encryption combinations
              </p>
            </div>
          </div>
        )}

        {(step === "unlocked" || step === "mounted" || step === "mounting") && volumeInfo && (
          <>
            <VolumeInfoPanel info={volumeInfo} fsType={fsType} filePath={filePath} />

            <FileBrowser
              files={files}
              onExtractAll={handleExtractAll}
              onExtractSelected={handleExtractSelected}
              isExtracting={isExtracting}
              progress={extractProgress}
            />

            <MountPanel
              isMounted={step === "mounted"}
              isMounting={isMounting}
              isUnmounting={isUnmounting}
              mountProgress={mountProgress}
              mountStage={mountStage}
              driveLetter={driveLetter}
              onMount={handleMount}
              onUnmount={handleUnmount}
            />
          </>
        )}
      </div>

      <Toaster position="bottom-right" />
    </div>
  );
}

export default App;
