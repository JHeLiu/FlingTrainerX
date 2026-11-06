global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;

// MVVM 统一用 CommunityToolkit.Mvvm：
// ObservableObject / [ObservableProperty] 负责属性通知，RelayCommand / AsyncRelayCommand 负责命令，
// 不再自己手写 INotifyPropertyChanged 和 ICommand 实现。
global using CommunityToolkit.Mvvm.ComponentModel;
global using CommunityToolkit.Mvvm.Input;
