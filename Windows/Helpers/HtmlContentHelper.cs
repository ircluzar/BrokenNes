namespace BrokenNes.Windows.Helpers
{
    public static class HtmlContentHelper
    {
        public static string GetWidgetModeHtml()
        {
            return $@"
                        <!DOCTYPE html>
                        <html>
                        <head>
                            <style>
                                body {{
                                    margin: 0;
                                    padding: 20px;
                                    background: transparent;
                                    font-family: 'Segoe UI', Arial, sans-serif;
                                    color: white;
                                    overflow: hidden;
                                    display: flex;
                                    align-items: stretch;
                                    height: calc(100vh - 40px);
                                    box-sizing: border-box;
                                }}
                                .widget-panel {{
                                    flex: 1;
                                    background: rgba(20, 20, 30, 0.85);
                                    backdrop-filter: blur(10px);
                                    display: flex;
                                    justify-content: center;
                                    align-items: center;
                                    flex-direction: column;
                                    border-radius: 16px;
                                    border: 2px solid rgba(255, 255, 255, 0.1);
                                    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
                                }}
                                .widget-content {{
                                    text-align: center;
                                    padding: 30px;
                                }}
                                h1 {{
                                    font-size: 32px;
                                    margin-bottom: 15px;
                                    font-weight: 600;
                                }}
                                p {{
                                    font-size: 16px;
                                    opacity: 0.8;
                                    line-height: 1.6;
                                }}
                            </style>
                        </head>
                        <body>
                            <div class='widget-panel'>
                                <div class='widget-content'>
                                    <h1>Widget Panel</h1>
                                    <p>Background renders underneath<br/>with transparent HTML overlay</p>
                                </div>
                            </div>
                        </body>
                        </html>";
        }

        public static string GetOverlayModeHtml()
        {
            return @"
                        <!DOCTYPE html>
                        <html>
                        <head>
                            <style>
                                body {
                                    margin: 0;
                                    padding: 0;
                                    background: transparent;
                                    font-family: 'Segoe UI', Arial, sans-serif;
                                }
                                .floating-box {
                                    position: absolute;
                                    top: 50%;
                                    left: 50%;
                                    transform: translate(-50%, -50%);
                                    background: rgba(30, 144, 255, 0.9);
                                    color: white;
                                    padding: 30px 50px;
                                    border-radius: 15px;
                                    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
                                    text-align: center;
                                    font-size: 24px;
                                    font-weight: bold;
                                    backdrop-filter: blur(10px);
                                    border: 2px solid rgba(255, 255, 255, 0.3);
                                }
                                .subtitle {
                                    font-size: 14px;
                                    margin-top: 10px;
                                    opacity: 0.9;
                                    font-weight: normal;
                                }
                            </style>
                        </head>
                        <body>
                            <div class='floating-box'>
                                HTML Overlay
                                <div class='subtitle'>Floating over DirectX render</div>
                            </div>
                        </body>
                        </html>";
        }
    }
}
