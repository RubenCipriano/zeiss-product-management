import { Component } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { Icon } from './shared/ui/icon/icon';
import { ToastOutlet } from './shared/ui/toasts/toast-outlet';

/** Application shell: top bar, routed content and the notifications outlet. */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, Icon, ToastOutlet],
  templateUrl: './app.html',
  host: { class: 'flex min-h-full flex-col' },
})
export class App {}
