import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { Auth } from './core/auth/auth';
import { Icon } from './shared/ui/icon/icon';
import { ToastOutlet } from './shared/ui/toasts/toast-outlet';

/** Application shell: top bar (with the signed-in user), routed content and the notifications outlet. */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, Icon, ToastOutlet],
  templateUrl: './app.html',
  host: { class: 'flex min-h-full flex-col' },
})
export class App {
  protected readonly auth = inject(Auth);
  private readonly router = inject(Router);

  protected signOut(): void {
    this.auth.logout();
    void this.router.navigate(['/login']);
  }
}
