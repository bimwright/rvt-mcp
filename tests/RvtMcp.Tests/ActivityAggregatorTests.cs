using System;
using RvtMcp.Plugin.Views.Toast;
using Xunit;

namespace RvtMcp.Tests
{
    public class ActivityAggregatorTests
    {
        private sealed class FakeClock
        {
            public TimeSpan Now;
            public void At(double seconds) => Now = TimeSpan.FromSeconds(seconds);
        }

        private readonly FakeClock _clock = new FakeClock();
        private int _idleSeconds = 20;

        private ActivityAggregator Make() => new ActivityAggregator(() => _idleSeconds, () => _clock.Now);

        private static void Ok(ActivityAggregator a, bool image = false, bool usable = true) =>
            a.RecordResult("Create Level", "Level 2 created", true, image, usable);

        private static void Fail(ActivityAggregator a, bool image = false, bool usable = true) =>
            a.RecordResult("Create Grid", "No document is open.", false, image, usable);

        private static ActivitySnapshot Visible(ActivityAggregator a)
        {
            var r = a.TakeRender();
            Assert.Equal(ActivityCardPhase.Visible, r.Phase);
            return r.Card;
        }

        // --- counting -------------------------------------------------------

        [Fact]
        public void Burst_counts_on_one_card_and_images_only_on_success()
        {
            var a = Make();
            for (var i = 0; i < 6; i++) Ok(a);
            Ok(a, image: true);
            Ok(a, image: true);
            Fail(a, image: true);

            var card = Visible(a);
            Assert.Equal(8, card.Succeeded);
            Assert.Equal(1, card.Failed);
            Assert.Equal(2, card.Images);
            Assert.False(card.IsStatus);
            Assert.Equal("Create Grid", card.Title);
            Assert.Equal("No document is open.", card.Body);
            Assert.False(card.LatestSuccess);
        }

        [Fact]
        public void HasFailure_stays_after_later_successes()
        {
            var a = Make();
            Fail(a);
            Ok(a);

            var card = Visible(a);
            Assert.True(card.HasFailure);
            Assert.True(card.LatestSuccess);
        }

        [Fact]
        public void Burst_asks_for_one_render_until_it_runs()
        {
            var a = Make();

            Assert.True(a.RecordResult("A", null, true, false, true));
            Assert.False(a.RecordResult("B", null, true, false, true));
            Assert.False(a.RecordResult("C", null, true, false, true));

            Assert.Equal(3, Visible(a).Succeeded);
            Assert.True(a.RecordResult("D", null, true, false, true));
        }

        // --- deadline -------------------------------------------------------

        [Fact]
        public void Card_closes_N_seconds_after_the_last_result()
        {
            var a = Make();
            Ok(a);
            a.TakeRender();

            _clock.At(19.9);
            Assert.False(a.Tick(frameUsable: true));
            _clock.At(20);
            Assert.True(a.Tick(frameUsable: true));
            Assert.Equal(ActivityCardPhase.Closing, a.TakeRender().Phase);
        }

        [Fact]
        public void New_result_resets_the_deadline()
        {
            var a = Make();
            Ok(a);
            _clock.At(15);
            Ok(a);
            a.TakeRender();

            _clock.At(34.9);
            Assert.False(a.Tick(true));
            _clock.At(35);
            Assert.True(a.Tick(true));
        }

        [Fact]
        public void Changed_N_applies_from_the_next_rearm()
        {
            var a = Make();
            Ok(a);               // deadline 20
            _idleSeconds = 10;
            _clock.At(5);
            Ok(a);               // deadline 15
            a.TakeRender();

            _clock.At(14.9);
            Assert.False(a.Tick(true));
            _clock.At(15);
            Assert.True(a.Tick(true));
        }

        [Fact]
        public void Invalid_N_falls_back_to_the_default()
        {
            _idleSeconds = 0;
            var a = Make();
            Ok(a);
            a.TakeRender();

            _clock.At(19.9);
            Assert.False(a.Tick(true));
            _clock.At(20);
            Assert.True(a.Tick(true));
        }

        [Fact]
        public void Late_render_fades_an_expired_card_instead_of_showing_it()
        {
            var a = Make();
            Ok(a);

            _clock.At(25); // UI thread was busy past the deadline
            Assert.Equal(ActivityCardPhase.Closing, a.TakeRender().Phase);
        }

        [Fact]
        public void Rendering_and_ticking_never_extend_the_card()
        {
            var a = Make();
            Ok(a);
            for (var t = 1; t < 20; t++)
            {
                _clock.At(t);
                a.TakeRender();
                a.Tick(true);
            }

            _clock.At(20);
            Assert.True(a.Tick(true));
        }

        // --- hover ----------------------------------------------------------

        [Fact]
        public void Hover_stops_the_clock_and_leave_restarts_N()
        {
            var a = Make();
            Ok(a);
            var id = Visible(a).CardId;

            _clock.At(5);
            a.PointerEntered(id);
            _clock.At(100);
            Assert.False(a.Tick(true));

            a.PointerLeft(id);
            _clock.At(119.9);
            Assert.False(a.Tick(true));
            _clock.At(120);
            Assert.True(a.Tick(true));
        }

        [Fact]
        public void Result_while_hovering_keeps_the_card_paused()
        {
            var a = Make();
            Ok(a);
            var id = Visible(a).CardId;
            a.PointerEntered(id);

            _clock.At(10);
            Ok(a);
            _clock.At(60);
            Assert.False(a.Tick(true));
            Assert.Equal(2, Visible(a).Succeeded);
        }

        [Fact]
        public void Leave_without_enter_does_not_rearm()
        {
            var a = Make();
            Ok(a);
            var id = Visible(a).CardId;

            _clock.At(15);
            a.PointerLeft(id);
            _clock.At(20);
            Assert.True(a.Tick(true));
        }

        // --- card lifecycle -------------------------------------------------

        [Fact]
        public void Result_after_close_starts_a_new_card_from_zero()
        {
            var a = Make();
            Ok(a);
            Fail(a);
            var first = Visible(a).CardId;

            _clock.At(20);
            a.Tick(true);
            a.CardClosed(first);
            Assert.Equal(ActivityCardPhase.Hidden, a.TakeRender().Phase);

            _clock.At(30);
            Ok(a);
            var card = Visible(a);
            Assert.NotEqual(first, card.CardId);
            Assert.Equal(1, card.Succeeded);
            Assert.Equal(0, card.Failed);
            Assert.False(card.HasFailure);
        }

        [Fact]
        public void Result_while_closing_opens_a_new_card_and_the_old_close_is_ignored()
        {
            var a = Make();
            Ok(a);
            var first = Visible(a).CardId;
            _clock.At(20);
            a.Tick(true);

            _clock.At(20.1);
            Ok(a);
            var second = Visible(a).CardId;
            Assert.NotEqual(first, second);

            a.CardClosed(first); // the old window's fade finishes late
            Assert.Equal(second, Visible(a).CardId);
        }

        [Fact]
        public void Dismiss_closes_the_card()
        {
            var a = Make();
            Ok(a);
            var id = Visible(a).CardId;

            Assert.True(a.Dismiss(id));
            var r = a.TakeRender();
            Assert.Equal(ActivityCardPhase.Closing, r.Phase);
            Assert.Equal(id, r.Card.CardId);

            a.CardClosed(id);
            Assert.Equal(ActivityCardPhase.Hidden, a.TakeRender().Phase);
        }

        [Fact]
        public void Callbacks_for_an_old_card_are_ignored()
        {
            var a = Make();
            Ok(a);
            var old = Visible(a).CardId;
            a.Dismiss(old);
            a.CardClosed(old);
            Ok(a);
            var current = Visible(a).CardId;

            a.PointerEntered(old);
            Assert.False(a.Dismiss(old));
            a.CardClosed(old);

            _clock.At(20);
            Assert.True(a.Tick(true)); // not paused by the stale enter
            Assert.Equal(current, a.TakeRender().Card.CardId);
        }

        // --- minimize / modal ---------------------------------------------

        [Fact]
        public void Results_on_an_unusable_frame_are_counted_and_shown_once_on_restore()
        {
            var a = Make();
            Ok(a, usable: false);
            Fail(a, usable: false);
            Ok(a, image: true, usable: false);

            Assert.True(a.HasUnrenderedResults);
            Assert.Equal(ActivityCardPhase.Hidden, a.TakeRender().Phase);
            Assert.False(a.FlushIfUsable(frameUsable: false));

            _clock.At(300);
            Assert.True(a.FlushIfUsable(frameUsable: true));
            Assert.False(a.FlushIfUsable(frameUsable: true));
            Assert.False(a.HasUnrenderedResults);

            var card = Visible(a);
            Assert.Equal(2, card.Succeeded);
            Assert.Equal(1, card.Failed);
            Assert.Equal(1, card.Images);

            _clock.At(319.9);
            Assert.False(a.Tick(true));
            _clock.At(320);
            Assert.True(a.Tick(true));
        }

        [Fact]
        public void Visible_card_is_parked_while_the_frame_is_unusable_and_keeps_its_counts()
        {
            var a = Make();
            Ok(a);
            var id = Visible(a).CardId;

            _clock.At(5);
            Assert.True(a.Tick(frameUsable: false));
            Assert.Equal(ActivityCardPhase.Hidden, a.TakeRender().Phase);

            _clock.At(10);
            Ok(a, usable: false);
            _clock.At(500);
            a.FlushIfUsable(true);

            var card = Visible(a);
            Assert.Equal(id, card.CardId);
            Assert.Equal(2, card.Succeeded);
        }

        [Fact]
        public void Result_after_restore_shows_before_idling_flushes()
        {
            var a = Make();
            Ok(a, usable: false);
            Ok(a, usable: true);

            Assert.False(a.HasUnrenderedResults);
            Assert.Equal(2, Visible(a).Succeeded);
        }

        // --- status cards ---------------------------------------------------

        [Fact]
        public void Status_card_uses_its_own_duration()
        {
            var a = Make();
            Assert.True(a.ShowStatus("Toast on", "Notifications are on.", 3));

            var card = Visible(a);
            Assert.True(card.IsStatus);
            Assert.Equal("Toast on", card.Title);

            _clock.At(2.9);
            Assert.False(a.Tick(true));
            _clock.At(3);
            Assert.True(a.Tick(true));
        }

        [Fact]
        public void Status_card_is_not_parked_on_an_unusable_frame()
        {
            var a = Make();
            a.ShowStatus("Connected", null, 6);

            Assert.False(a.Tick(frameUsable: false));
            Assert.Equal(ActivityCardPhase.Visible, a.TakeRender().Phase);
        }

        [Fact]
        public void Status_card_does_not_cover_an_open_activity_card()
        {
            var a = Make();
            Ok(a);
            var id = Visible(a).CardId;

            Assert.False(a.ShowStatus("Connected", null, 6));
            Assert.Equal(id, Visible(a).CardId);

            var pending = Make();
            Ok(pending, usable: false);
            Assert.False(pending.ShowStatus("Connected", null, 6));
            Assert.True(pending.HasUnrenderedResults);
        }

        [Fact]
        public void Result_replaces_a_status_card_with_an_activity_card()
        {
            var a = Make();
            a.ShowStatus("Connected", null, 6);
            var status = Visible(a).CardId;

            Ok(a);
            var card = Visible(a);
            Assert.NotEqual(status, card.CardId);
            Assert.False(card.IsStatus);
            Assert.Equal(1, card.Succeeded);
        }

        [Fact]
        public void Rapid_status_cards_replace_each_other()
        {
            var a = Make();
            a.ShowStatus("Toast on", null, 3);
            var first = Visible(a).CardId;
            a.ShowStatus("Toast off", null, 3);

            var card = Visible(a);
            Assert.NotEqual(first, card.CardId);
            Assert.Equal("Toast off", card.Title);
        }

        // --- on / off -------------------------------------------------------

        [Fact]
        public void Off_drops_the_card_so_a_late_render_shows_only_the_off_card()
        {
            var a = Make();
            Assert.True(a.RecordResult("A", null, true, false, true)); // render posted, not yet run

            a.Reset();
            a.ShowStatus("Toast off", null, 3);

            var card = Visible(a); // the render posted before Off runs now
            Assert.True(card.IsStatus);
            Assert.Equal("Toast off", card.Title);
        }

        [Fact]
        public void Off_clears_counts_and_pending_results()
        {
            var a = Make();
            Ok(a, usable: false);
            a.Reset();
            Assert.False(a.HasUnrenderedResults);
            Assert.False(a.FlushIfUsable(true));

            Ok(a);
            Assert.Equal(1, Visible(a).Succeeded);
        }

        [Fact]
        public void Reset_with_no_card_asks_for_no_render()
        {
            Assert.False(Make().Reset());
        }
    }
}
