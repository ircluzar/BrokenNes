function Save-WindowScreenshotByHandle {
    param([IntPtr]$Hwnd, [string]$OutPath)
    $rect = New-Object BrokenNesUat.DwmUat+RECT
    [BrokenNesUat.DwmUat]::DwmGetWindowAttribute($Hwnd, 9, [ref]$rect, [System.Runtime.InteropServices.Marshal]::SizeOf([type][BrokenNesUat.DwmUat+RECT])) | Out-Null
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) { return $false }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [BrokenNesUat.DwmUat]::PrintWindow($Hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    if (-not $ok) { $g.Dispose(); $bmp.Dispose(); return $false }
    $bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return $true
}
