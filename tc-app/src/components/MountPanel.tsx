import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Progress } from "@/components/ui/progress";
import { HardDrive, CircleStop, ShieldAlert, Loader2 } from "lucide-react";

interface MountPanelProps {
  isMounted: boolean;
  isMounting: boolean;
  isUnmounting: boolean;
  mountProgress: number;
  mountStage: string;
  driveLetter: string | null;
  onMount: () => void;
  onUnmount: () => void;
}

function stageLabel(stage: string): string {
  switch (stage) {
    case "creating_vhd":
      return "Creating virtual disk…";
    case "mounting":
      return "Mounting drive…";
    case "detecting":
      return "Detecting drive letter…";
    default:
      return "Preparing…";
  }
}

export function MountPanel({
  isMounted,
  isMounting,
  isUnmounting,
  mountProgress,
  mountStage,
  driveLetter,
  onMount,
  onUnmount,
}: MountPanelProps) {
  return (
    <Card>
      <CardContent className="py-4">
        <div className="space-y-3">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-3">
              <HardDrive className="h-5 w-5 text-muted-foreground" />
              <div>
                <p className="text-sm font-medium">Mount as Drive</p>
                {isMounting ? (
                  <p className="text-xs text-blue-600 flex items-center gap-1">
                    <Loader2 className="h-3 w-3 animate-spin" />
                    {stageLabel(mountStage)}
                  </p>
                ) : isUnmounting ? (
                  <p className="text-xs text-amber-600 flex items-center gap-1">
                    <Loader2 className="h-3 w-3 animate-spin" />
                    Unmounting and saving changes…
                  </p>
                ) : isMounted && driveLetter ? (
                  <p className="text-xs text-green-600">
                    Mounted as {driveLetter}
                  </p>
                ) : isMounted ? (
                  <p className="text-xs text-amber-600">
                    Mounted (no drive letter assigned)
                  </p>
                ) : (
                  <p className="text-xs text-muted-foreground flex items-center gap-1">
                    <ShieldAlert className="h-3 w-3" />
                    Requires Administrator privileges
                  </p>
                )}
              </div>
            </div>

            {isMounting || isUnmounting ? (
              <Button size="sm" disabled>
                <Loader2 className="h-3.5 w-3.5 mr-1 animate-spin" />
                {isMounting ? "Mounting…" : "Unmounting…"}
              </Button>
            ) : isMounted ? (
              <Button variant="outline" size="sm" onClick={onUnmount}>
                <CircleStop className="h-3.5 w-3.5 mr-1" />
                Unmount & Save
              </Button>
            ) : (
              <Button size="sm" onClick={onMount}>
                <HardDrive className="h-3.5 w-3.5 mr-1" />
                Mount as Drive
              </Button>
            )}
          </div>

          {isMounting && (
            <div className="space-y-1">
              <Progress
                value={mountProgress * 100}
                className="h-2"
              />
              <p className="text-xs text-muted-foreground text-right">
                {Math.round(mountProgress * 100)}%
              </p>
            </div>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
