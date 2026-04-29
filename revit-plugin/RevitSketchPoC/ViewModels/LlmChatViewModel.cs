using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using RevitSketchPoC.Services;

namespace RevitSketchPoC.ViewModels
{
    public sealed class ChatLine : INotifyPropertyChanged
    {
        private string _text = string.Empty;

        public bool IsUser { get; set; }

        public string Speaker => IsUser ? "You" : "Assistant";

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
        private string _input = string.Empty;
        private bool _isBusy;
        private readonly RelayCommand _sendCommand;

        public ObservableCollection<ChatLine> Messages { get; } = new ObservableCollection<ChatLine>();

        public event PropertyChangedEventHandler? PropertyChanged;

        public LlmChatViewModel(LlmChatService chat)
        {
            _chat = chat;
            _sendCommand = new RelayCommand(_ => _ = SendAsync(), _ => CanSend());
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
            }
        }

        public ICommand SendCommand => _sendCommand;

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
                var reply = await Task.Run(async () => await _chat.CompleteAsync(turns).ConfigureAwait(false))
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
