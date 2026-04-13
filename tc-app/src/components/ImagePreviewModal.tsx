import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { ImageIcon } from "lucide-react";

interface ImagePreviewModalProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  volumePath: string | null;
  fileName: string;
}

interface ImagePreviewResult {
  data: string;
  mime_type: string;
}

export function ImagePreviewModal({
  open,
  onOpenChange,
  volumePath,
  fileName,
}: ImagePreviewModalProps) {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [imageSrc, setImageSrc] = useState<string | null>(null);

  useEffect(() => {
    if (!open || !volumePath) {
      setImageSrc(null);
      setError(null);
      return;
    }

    let cancelled = false;
    setLoading(true);
    setError(null);

    invoke<ImagePreviewResult>("preview_image", {
      volumePath,
    })
      .then((result) => {
        if (!cancelled) {
          setImageSrc(`data:${result.mime_type};base64,${result.data}`);
        }
      })
      .catch((e) => {
        if (!cancelled) {
          setError(String(e));
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [open, volumePath]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2 text-sm">
            <ImageIcon className="h-4 w-4" />
            {fileName}
          </DialogTitle>
        </DialogHeader>

        <div className="flex items-center justify-center min-h-[200px]">
          {loading && (
            <div className="text-center space-y-3">
              <div className="animate-spin h-8 w-8 border-2 border-primary border-t-transparent rounded-full mx-auto" />
              <p className="text-sm text-muted-foreground">
                Loading preview...
              </p>
            </div>
          )}

          {error && (
            <div className="text-center space-y-2 px-4">
              <ImageIcon className="h-10 w-10 mx-auto text-muted-foreground" />
              <p className="text-sm text-destructive">
                Failed to load preview
              </p>
              <p className="text-xs text-muted-foreground">{error}</p>
            </div>
          )}

          {imageSrc && !loading && (
            <img
              src={imageSrc}
              alt={fileName}
              className="max-w-full max-h-[60vh] object-contain rounded"
            />
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}
