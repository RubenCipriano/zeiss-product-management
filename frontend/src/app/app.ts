import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { Auth } from './core/auth/auth';
import { Icon } from './shared/ui/icon/icon';
import { ToastOutlet } from './shared/ui/toasts/toast-outlet';

/**
 * The app shell, shown on every screen: the top bar (signed-in user, Sign out), the current page (`<router-outlet>`)
 * and the toast messages. Like every component here it is standalone: `imports` lists what its template uses, with no
 * NgModule. `inject()` asks Angular for a shared service instance (dependency injection).
 */
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
