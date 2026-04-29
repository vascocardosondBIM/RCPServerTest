using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RevitSketchPoC.App;
using RevitSketchPoC.Core.ViewModels;
using RevitSketchPoC.Chat.Contracts;
using RevitSketchPoC.Chat.Services;

namespace RevitSketchPoC.Chat.ViewModels
{
    public sealed class ChatLine : INotifyPropertyChanged
    {
        private string _text = string.Empty;
        private string? _imageAttachmentPath;
        private BitmapImage? _imagePreview;

        public bool IsUser { get; set; }

        public string Speaker => IsUser ? "Tu" : "Assistente";

        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Local file path for an image attached to this user message.</summary>
        public string? ImageAttachmentPath
        {
            get => _imageAttachmentPath;
            set
            {
                _imageAttachmentPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasImage));
                RebuildImagePreview();
            }
        }

        public bool HasImage => !string.IsNullOrWhiteSpace(ImageAttachmentPath) &&
                                File.Exists(ImageAttachmentPath ?? string.Empty);

        public BitmapImage? ImagePreview
        {
            get => _imagePreview;
            private set
            {
                _imagePreview = value;
                OnPropertyChanged();
            }
        }

        private void RebuildImagePreview()
        {
            if (!HasImage || ImageAttachmentPath == null)
            {
                ImagePreview = null;
                return;
            }

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(ImageAttachmentPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                ImagePreview = bmp;
            }
            catch
            {
                ImagePreview = null;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class LlmChatViewModel : INotifyPropertyChanged
    {
        private const long MaxImageBytes = 6 * 1024 * 1024;

        private readonly LlmChatService _chat;
        private readonly UIDocument _uidoc;
        private readonly Dispatcher _uiDispatcher;
        private string _input = string.Empty;
        private bool _isBusy;
        private string _projectContext = string.Empty;
        private string _selectionContext = string.Empty;
        private string _contextHint = "A carregar contexto do projetoâ€¦";
        private string? _pendingImagePath;
        private readonly RelayCommand _sendCommand;
        private readonly RelayCommand _refreshProjectCommand;
        private readonly RelayCommand _includeSelectionCommand;
        private readonly RelayCommand _clearSelectionContextCommand;
        private readonly RelayCommand _attachImageCommand;
        private readonly RelayCommand _clearPendingImageCommand;

        public ObservableCollection<ChatLine> Messages { get; } = new ObservableCollection<ChatLine>();

        public event PropertyChangedEventHandler? PropertyChanged;

        public LlmChatViewModel(LlmChatService chat, UIDocument uidoc, Dispatcher uiDispatcher)
        {
            _chat = chat;
            _uidoc = uidoc;
            _uiDispatcher = uiDispatcher;
            _sendCommand = new RelayCommand(_ => _ = SendAsync(), _ => CanSend());
            _refreshProjectCommand = new RelayCommand(_ => RefreshProjectContext(), _ => !IsBusy && IsDocumentAlive());
            _includeSelectionCommand = new RelayCommand(_ => IncludeSelectionContext(), _ => !IsBusy && IsDocumentAlive());
            _clearSelectionContextCommand = new RelayCommand(_ => ClearSelectionContext(), _ => !IsBusy);
            _attachImageCommand = new RelayCommand(_ => AttachImage(), _ => !IsBusy);
            _clearPendingImageCommand = new RelayCommand(_ => ClearPendingImage(), _ => !IsBusy && HasPendingImage);

            RefreshProjectContext();
        }

        public string? PendingImagePath
        {
            get => _pendingImagePath;
            private set
            {
                _pendingImagePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasPendingImage));
                OnPropertyChanged(nameof(PendingImageHint));
                _sendCommand.RaiseCanExecuteChanged();
                _clearPendingImageCommand.RaiseCanExecuteChanged();
            }
        }

        public bool HasPendingImage => !string.IsNullOrWhiteSpace(PendingImagePath) &&
                                       File.Exists(PendingImagePath!);

        public string PendingImageHint =>
            HasPendingImage ? "Imagem: " + Path.GetFileName(PendingImagePath!) : "Sem imagem anexada.";

        public string Input
        {
            get => _input;
            set
            {
                _input = value;
                OnPropertyChanged();
                _sendCommand.RaiseCanExecuteChanged();
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
                OnPropertyChanged();
                _sendCommand.RaiseCanExecuteChanged();
                _refreshProjectCommand.RaiseCanExecuteChanged();
                _includeSelectionCommand.RaiseCanExecuteChanged();
                _clearSelectionContextCommand.RaiseCanExecuteChanged();
                _attachImageCommand.RaiseCanExecuteChanged();
                _clearPendingImageCommand.RaiseCanExecuteChanged();
            }
        }

        public string ContextHint
        {
            get => _contextHint;
            private set
            {
                _contextHint = value;
                OnPropertyChanged();
            }
        }

        public ICommand SendCommand => _sendCommand;
        public ICommand RefreshProjectContextCommand => _refreshProjectCommand;
        public ICommand IncludeSelectionContextCommand => _includeSelectionCommand;
        public ICommand ClearSelectionContextCommand => _clearSelectionContextCommand;
        public ICommand AttachImageCommand => _attachImageCommand;
        public ICommand ClearPendingImageCommand => _clearPendingImageCommand;

        private bool IsDocumentAlive()
        {
            try
            {
                var doc = _uidoc.Document;
                return doc != null;
            }
            catch
            {
                return false;
            }
        }

        public void RefreshProjectContext()
        {
            try
            {
                if (!IsDocumentAlive())
                {
                    ContextHint = "Documento nÃ£o disponÃ­vel.";
                    _projectContext = string.Empty;
                    return;
                }

                _projectContext = RevitChatContextBuilder.BuildProjectSnapshot(_uidoc);
                ContextHint = "Contexto do projeto atualizado (" + DateTime.Now.ToString("HH:mm") + ").";
            }
            catch (Exception ex)
            {
                _projectContext = string.Empty;
                ContextHint = "Erro ao ler projeto: " + ex.Message;
            }
        }

        public void IncludeSelectionContext()
        {
            try
            {
                if (!IsDocumentAlive())
                {
                    ContextHint = "Documento nÃ£o disponÃ­vel.";
                    return;
                }

                var n = _uidoc.Selection.GetElementIds().Count;
                if (n == 0)
                {
                    ContextHint = "Seleciona um ou mais elementos no Revit e volta a carregar.";
                    return;
                }

                _selectionContext = RevitChatContextBuilder.BuildSelectionSnapshot(_uidoc);
                ContextHint = "SeleÃ§Ã£o incluÃ­da no contexto (" + n + " elemento(s)) â€” " + DateTime.Now.ToString("HH:mm") + ".";
            }
            catch (Exception ex)
            {
                ContextHint = "Erro ao ler seleÃ§Ã£o: " + ex.Message;
            }
        }

        public void ClearSelectionContext()
        {
            _selectionContext = string.Empty;
            ContextHint = "Contexto de seleÃ§Ã£o limpo.";
        }

        private void AttachImage()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Imagens|*.png;*.jpg;*.jpeg;*.webp;*.bmp|Todos|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var info = new FileInfo(dlg.FileName);
                    if (info.Length > MaxImageBytes)
                    {
                        ContextHint = "Imagem demasiado grande (mÃ¡x. ~6 MB).";
                        return;
                    }

                    PendingImagePath = dlg.FileName;
                    ContextHint = "Imagem pronta para enviar com a prÃ³xima mensagem.";
                }
                catch (Exception ex)
                {
                    ContextHint = "Erro ao ler ficheiro: " + ex.Message;
                }
            }
        }

        private void ClearPendingImage()
        {
            PendingImagePath = null;
        }

        private string BuildRevitContextForApi()
        {
            var parts = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(_projectContext))
            {
                parts.AppendLine("#### Project snapshot (JSON)");
                parts.AppendLine(_projectContext.Trim());
            }

            if (!string.IsNullOrWhiteSpace(_selectionContext))
            {
                parts.AppendLine();
                parts.AppendLine("#### Selection snapshot (JSON)");
                parts.AppendLine(_selectionContext.Trim());
            }

            var s = parts.ToString().Trim();
            return string.IsNullOrEmpty(s) ? string.Empty : s;
        }

        private bool CanSend()
        {
            return !IsBusy && (!string.IsNullOrWhiteSpace(Input) || HasPendingImage);
        }

        private static (string base64, string mime)? LoadImageForApi(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            var mime = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "image/png"
            };

            var bytes = File.ReadAllBytes(path);
            return (Convert.ToBase64String(bytes), mime);
        }

        private List<ChatLlmTurn> BuildApiTurns()
        {
            var list = new List<ChatLlmTurn>();
            foreach (var line in Messages)
            {
                if (line.IsUser)
                {
                    var turn = new ChatLlmTurn { IsUser = true, Text = line.Text ?? string.Empty };
                    if (line.HasImage && line.ImageAttachmentPath != null)
                    {
                        var img = LoadImageForApi(line.ImageAttachmentPath);
                        if (img != null)
                        {
                            turn.ImageBase64 = img.Value.base64;
                            turn.ImageMimeType = img.Value.mime;
                        }
                    }

                    list.Add(turn);
                }
                else
                {
                    list.Add(new ChatLlmTurn { IsUser = false, Text = line.Text ?? string.Empty });
                }
            }

            return list;
        }

        private void TryEnqueueRevitOps(string assistantReply)
        {
            var ops = ChatRevitOpsParser.TryExtractRevitOps(assistantReply);
            if (ops == null || ops.Count == 0)
            {
                return;
            }

            var handler = SketchToBimApp.ChatApplyOpsHandler;
            var ev = SketchToBimApp.ChatApplyOpsEvent;
            if (handler == null || ev == null)
            {
                Messages.Add(new ChatLine
                {
                    IsUser = false,
                    Text = "[Revit] Add-in nÃ£o expÃ´s ExternalEvent para executar operaÃ§Ãµes."
                });
                return;
            }

            handler.Prepare(
                _uidoc,
                ops,
                _uiDispatcher,
                summary => Messages.Add(new ChatLine { IsUser = false, Text = "[Revit] " + summary }));
            ev.Raise();
        }

        private async Task SendAsync()
        {
            var userText = (Input ?? string.Empty).Trim();
            if (IsBusy)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(userText) && !HasPendingImage)
            {
                return;
            }

            var displayText = string.IsNullOrWhiteSpace(userText) ? "(imagem)" : userText;
            Input = string.Empty;
            var line = new ChatLine
            {
                IsUser = true,
                Text = displayText,
                ImageAttachmentPath = PendingImagePath
            };
            Messages.Add(line);
            PendingImagePath = null;
            IsBusy = true;

            try
            {
                var turns = BuildApiTurns();
                var revitCtx = BuildRevitContextForApi();
                var reply = await Task.Run(async () =>
                        await _chat.CompleteAsync(turns, string.IsNullOrWhiteSpace(revitCtx) ? null : revitCtx).ConfigureAwait(false))
                    .ConfigureAwait(true);
                Messages.Add(new ChatLine { IsUser = false, Text = reply });
                TryEnqueueRevitOps(reply);
            }
            catch (Exception ex)
            {
                Messages.Add(new ChatLine { IsUser = false, Text = "Erro: " + ex.Message });
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
