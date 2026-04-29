using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Autodesk.Revit.UI;
using RevitSketchPoC.Services;

namespace RevitSketchPoC.ViewModels
{
    public sealed class ChatLine : INotifyPropertyChanged
    {
        private string _text = string.Empty;

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

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class LlmChatViewModel : INotifyPropertyChanged
    {
        private readonly LlmChatService _chat;
        private readonly UIDocument _uidoc;
        private string _input = string.Empty;
        private bool _isBusy;
        private string _projectContext = string.Empty;
        private string _selectionContext = string.Empty;
        private string _contextHint = "A carregar contexto do projeto…";
        private readonly RelayCommand _sendCommand;
        private readonly RelayCommand _refreshProjectCommand;
        private readonly RelayCommand _includeSelectionCommand;
        private readonly RelayCommand _clearSelectionContextCommand;

        public ObservableCollection<ChatLine> Messages { get; } = new ObservableCollection<ChatLine>();

        public event PropertyChangedEventHandler? PropertyChanged;

        public LlmChatViewModel(LlmChatService chat, UIDocument uidoc)
        {
            _chat = chat;
            _uidoc = uidoc;
            _sendCommand = new RelayCommand(_ => _ = SendAsync(), _ => CanSend());
            _refreshProjectCommand = new RelayCommand(_ => RefreshProjectContext(), _ => !IsBusy && IsDocumentAlive());
            _includeSelectionCommand = new RelayCommand(_ => IncludeSelectionContext(), _ => !IsBusy && IsDocumentAlive());
            _clearSelectionContextCommand = new RelayCommand(_ => ClearSelectionContext(), _ => !IsBusy);

            RefreshProjectContext();
        }

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
                    ContextHint = "Documento não disponível.";
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
                    ContextHint = "Documento não disponível.";
                    return;
                }

                var n = _uidoc.Selection.GetElementIds().Count;
                if (n == 0)
                {
                    ContextHint = "Seleciona um ou mais elementos no Revit e volta a carregar.";
                    return;
                }

                _selectionContext = RevitChatContextBuilder.BuildSelectionSnapshot(_uidoc);
                ContextHint = "Seleção incluída no contexto (" + n + " elemento(s)) — " + DateTime.Now.ToString("HH:mm") + ".";
            }
            catch (Exception ex)
            {
                ContextHint = "Erro ao ler seleção: " + ex.Message;
            }
        }

        public void ClearSelectionContext()
        {
            _selectionContext = string.Empty;
            ContextHint = "Contexto de seleção limpo.";
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
            return !IsBusy && !string.IsNullOrWhiteSpace(Input);
        }

        private async Task SendAsync()
        {
            var userText = Input.Trim();
            if (string.IsNullOrEmpty(userText) || IsBusy)
            {
                return;
            }

            Input = string.Empty;
            Messages.Add(new ChatLine { IsUser = true, Text = userText });
            IsBusy = true;

            try
            {
                var turns = Messages.Select(m => (m.IsUser, m.Text)).ToList();
                var revitCtx = BuildRevitContextForApi();
                var reply = await Task.Run(async () =>
                        await _chat.CompleteAsync(turns, string.IsNullOrWhiteSpace(revitCtx) ? null : revitCtx).ConfigureAwait(false))
                    .ConfigureAwait(true);
                Messages.Add(new ChatLine { IsUser = false, Text = reply });
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
