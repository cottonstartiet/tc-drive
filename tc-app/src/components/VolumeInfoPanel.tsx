import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Shield, HardDrive, Hash, Info } from "lucide-react";
import type { VolumeInfo } from "@/App";

interface VolumeInfoPanelProps {
  info: VolumeInfo;
  fsType: string;
  filePath: string;
}

function formatSize(bytes: number): string {
  if (bytes >= 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`;
  if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${bytes} B`;
}

export function VolumeInfoPanel({ info, fsType, filePath }: VolumeInfoPanelProps) {
  const fileName = filePath.split("\\").pop() || filePath;

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="text-base flex items-center gap-2">
          <Info className="h-4 w-4" />
          Volume Details
          {info.is_hidden && (
            <span className="text-xs bg-amber-100 text-amber-700 px-2 py-0.5 rounded-full">
              Hidden Volume
            </span>
          )}
        </CardTitle>
      </CardHeader>
      <CardContent>
        <div className="grid grid-cols-2 gap-x-8 gap-y-2 text-sm">
          <div className="flex items-center gap-2 text-muted-foreground">
            <HardDrive className="h-3.5 w-3.5" />
            File
          </div>
          <div className="font-medium truncate" title={filePath}>{fileName}</div>

          <div className="flex items-center gap-2 text-muted-foreground">
            <Shield className="h-3.5 w-3.5" />
            Encryption
          </div>
          <div className="font-medium">{info.encryption}</div>

          <div className="flex items-center gap-2 text-muted-foreground">
            <Hash className="h-3.5 w-3.5" />
            Hash Algorithm
          </div>
          <div className="font-medium">{info.hash}</div>

          <div className="text-muted-foreground">Volume Size</div>
          <div className="font-medium">{formatSize(info.volume_size)}</div>

          <div className="text-muted-foreground">Filesystem</div>
          <div className="font-medium">{fsType || "Unknown"}</div>

          <div className="text-muted-foreground">Header Version</div>
          <div className="font-medium">{info.header_version}</div>

          <div className="text-muted-foreground">Sector Size</div>
          <div className="font-medium">{info.sector_size} bytes</div>
        </div>
      </CardContent>
    </Card>
  );
}
