import { useState, useMemo } from "react";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Progress } from "@/components/ui/progress";
import { ScrollArea } from "@/components/ui/scroll-area";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { FolderOpen, File, Download, CheckSquare, Square, Eye } from "lucide-react";
import { ImagePreviewModal } from "@/components/ImagePreviewModal";
import type { FileEntry } from "@/App";

const IMAGE_EXTENSIONS = new Set([
  "jpg", "jpeg", "png", "gif", "bmp", "webp", "svg", "ico", "tiff", "tif",
]);

function isImageFile(name: string): boolean {
  const ext = name.split(".").pop()?.toLowerCase() ?? "";
  return IMAGE_EXTENSIONS.has(ext);
}

interface FileBrowserProps {
  files: FileEntry[];
  onExtractAll: () => void;
  onExtractSelected: (paths: string[]) => void;
  isExtracting: boolean;
  progress: number;
}

function formatSize(bytes: number): string {
  if (bytes >= 1024 * 1024 * 1024)
    return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`;
  if (bytes >= 1024 * 1024)
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${bytes} B`;
}

export function FileBrowser({
  files,
  onExtractAll,
  onExtractSelected,
  isExtracting,
  progress,
}: FileBrowserProps) {
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [previewFile, setPreviewFile] = useState<{ path: string; name: string } | null>(null);

  const sortedFiles = useMemo(() => {
    return [...files].sort((a, b) => {
      if (a.is_dir !== b.is_dir) return a.is_dir ? -1 : 1;
      return a.name.localeCompare(b.name);
    });
  }, [files]);

  const fileCount = files.filter((f) => !f.is_dir).length;
  const dirCount = files.filter((f) => f.is_dir).length;

  const toggleSelect = (path: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(path)) {
        next.delete(path);
      } else {
        next.add(path);
      }
      return next;
    });
  };

  const selectAll = () => {
    const filePaths = files.filter((f) => !f.is_dir).map((f) => f.path);
    setSelected(new Set(filePaths));
  };

  const selectNone = () => setSelected(new Set());

  const handleExtractSelected = () => {
    onExtractSelected(Array.from(selected));
  };

  return (
    <>
    <Card>
      <CardHeader className="pb-3">
        <div className="flex items-center justify-between">
          <CardTitle className="text-base flex items-center gap-2">
            <FolderOpen className="h-4 w-4" />
            Files
            <span className="text-xs text-muted-foreground font-normal">
              {fileCount} files, {dirCount} folders
            </span>
          </CardTitle>
          <div className="flex gap-2">
            <Button
              variant="ghost"
              size="sm"
              onClick={selected.size > 0 ? selectNone : selectAll}
            >
              {selected.size > 0 ? "Deselect All" : "Select All"}
            </Button>
            {selected.size > 0 && (
              <Button
                size="sm"
                variant="outline"
                onClick={handleExtractSelected}
                disabled={isExtracting}
              >
                <Download className="h-3.5 w-3.5 mr-1" />
                Extract Selected ({selected.size})
              </Button>
            )}
            <Button
              size="sm"
              onClick={onExtractAll}
              disabled={isExtracting || fileCount === 0}
            >
              <Download className="h-3.5 w-3.5 mr-1" />
              Extract All
            </Button>
          </div>
        </div>
      </CardHeader>
      <CardContent className="pt-0">
        {isExtracting && (
          <div className="mb-3 space-y-1">
            <Progress value={progress * 100} className="h-2" />
            <p className="text-xs text-muted-foreground">
              Extracting... {Math.round(progress * 100)}%
            </p>
          </div>
        )}

        {files.length === 0 ? (
          <p className="text-sm text-muted-foreground text-center py-8">
            No files found in volume, or filesystem not supported.
          </p>
        ) : (
          <ScrollArea className="h-[280px] rounded-md border">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="w-8"></TableHead>
                  <TableHead>Name</TableHead>
                  <TableHead className="w-24 text-right">Size</TableHead>
                  <TableHead className="w-48">Path</TableHead>
                  <TableHead className="w-10"></TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {sortedFiles.map((file) => (
                  <TableRow
                    key={file.path}
                    className="cursor-pointer"
                    onClick={() => !file.is_dir && toggleSelect(file.path)}
                  >
                    <TableCell className="py-1.5">
                      {!file.is_dir &&
                        (selected.has(file.path) ? (
                          <CheckSquare className="h-4 w-4 text-primary" />
                        ) : (
                          <Square className="h-4 w-4 text-muted-foreground" />
                        ))}
                    </TableCell>
                    <TableCell className="py-1.5">
                      <div className="flex items-center gap-2">
                        {file.is_dir ? (
                          <FolderOpen className="h-4 w-4 text-amber-500 shrink-0" />
                        ) : (
                          <File className="h-4 w-4 text-muted-foreground shrink-0" />
                        )}
                        <span className="truncate">{file.name}</span>
                      </div>
                    </TableCell>
                    <TableCell className="py-1.5 text-right text-muted-foreground">
                      {file.is_dir ? "—" : formatSize(file.size)}
                    </TableCell>
                    <TableCell className="py-1.5 text-muted-foreground text-xs truncate max-w-[12rem]">
                      {file.path}
                    </TableCell>
                    <TableCell className="py-1.5">
                      {!file.is_dir && isImageFile(file.name) && (
                        <Button
                          variant="ghost"
                          size="sm"
                          className="h-6 w-6 p-0"
                          title="Preview image"
                          onClick={(e) => {
                            e.stopPropagation();
                            setPreviewFile({ path: file.path, name: file.name });
                          }}
                        >
                          <Eye className="h-3.5 w-3.5" />
                        </Button>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </ScrollArea>
        )}
      </CardContent>
    </Card>

    <ImagePreviewModal
      open={previewFile !== null}
      onOpenChange={(open) => { if (!open) setPreviewFile(null); }}
      volumePath={previewFile?.path ?? null}
      fileName={previewFile?.name ?? ""}
    />
    </>
  );
}
