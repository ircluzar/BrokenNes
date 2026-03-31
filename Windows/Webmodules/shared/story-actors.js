(function() {
  'use strict';

  const defaultVoice = {
    speed: 125,
    variant: 'croak',
    voiceName: 'en-us'
  };

  const actorOrder = ['sloppy', 'binty', 'skully', 'jimmy'];
  const actorMap = {
    sloppy: {
      id: 'sloppy',
      name: 'Sloppy',
      romSuffix: 'sloppy',
      initials: 'S',
      portraitPath: '../shared/story-portraits/sloppy.png',
      story: {
        voice: {
          speed: 125,
          variant: 'croak',
          voiceName: 'en-us'
        },
        pages: [
          {
            page: 1,
            text: 'Sloppy had finally beaten the final console.'
          },
          {
            page: 2,
            text: 'Stuck with nothing else to play, he had to innovate.'
          },
          {
            page: 3,
            text: 'He salvaged the parts and started building something new.'
          },
          {
            page: 4,
            text: 'What a madman. Do you think it will work?'
          }
        ]
      }
    },
    binty: {
      id: 'binty',
      name: 'Binty',
      romSuffix: 'binty',
      initials: 'B',
      portraitPath: '../shared/story-portraits/binty.png',
      story: {
        voice: {
          speed: 125,
          variant: 'croak',
          voiceName: 'en-us'
        },
        pages: [
          {
            page: 1,
            text: 'Little Binty had beaten all of the plug and play consoles.'
          },
          {
            page: 2,
            text: 'If only he could find a new one that is cooler than the rest.'
          },
          {
            page: 3,
            text: 'So he took matters into his own hands.'
          },
          {
            page: 4,
            text: 'And now, he is ready to build his ultimate console.'
          }
        ]
      }
    },
    skully: {
      id: 'skully',
      name: 'Skully',
      romSuffix: 'skully',
      initials: 'K',
      portraitPath: '../shared/story-portraits/skully.png',
      story: {
        voice: {
          speed: 125,
          variant: 'croak',
          voiceName: 'en-us'
        },
        pages: [
          {
            page: 1,
            text: 'All that Skully wanted was a console that doesnt suck.'
          },
          {
            page: 2,
            text: 'But all the dollar store sells are terrible clones.'
          },
          {
            page: 3,
            text: 'So he got angry and bashed them angrily until they were nothing.'
          },
          {
            page: 4,
            text: 'Hopefully he can glue them back together.'
          }
        ]
      }
    },
    jimmy: {
      id: 'jimmy',
      name: 'Jimmy',
      romSuffix: 'jimmy',
      initials: 'J',
      portraitPath: '../shared/story-portraits/jimmy.png',
      story: {
        voice: {
          speed: 125,
          variant: 'croak',
          voiceName: 'en-us'
        },
        pages: [
          {
            page: 1,
            text: 'All that little Jimmy wanted was a functional video game console.'
          },
          {
            page: 2,
            text: 'But his mom would keep buying him janky clones instead.'
          },
          {
            page: 3,
            text: 'So little Jimmy broke them all into parts.'
          },
          {
            page: 4,
            text: 'And now, he is ready to build his ultimate console.'
          }
        ]
      }
    }
  };

  function getActor(actorId) {
    return actorMap[actorId] || actorMap.jimmy;
  }

  function getAllActors() {
    return actorOrder.map((actorId) => actorMap[actorId]);
  }

  function buildStoryUrl(actorId) {
    const actor = getActor(actorId);
    return `../Story/index.html?actor=${encodeURIComponent(actor.id)}`;
  }

  function renderCharacterCards(container, onSelect) {
    if (!container) {
      return;
    }

    container.innerHTML = '';
    getAllActors().forEach((actor) => {
      const card = document.createElement('button');
      card.type = 'button';
      card.className = 'story-character-card';
      card.dataset.actor = actor.id;
      card.setAttribute('aria-label', `Play story as ${actor.name}`);

      const portrait = document.createElement('span');
      portrait.className = 'story-character-portrait';
      portrait.dataset.actor = actor.id;
      portrait.setAttribute('aria-hidden', 'true');

      const portraitImage = document.createElement('img');
      portraitImage.className = 'story-character-portrait-image';
      portraitImage.alt = '';
      portraitImage.src = actor.portraitPath;
      portraitImage.loading = 'eager';
      portraitImage.decoding = 'async';

      const portraitFallback = document.createElement('span');
      portraitFallback.className = 'story-character-portrait-fallback';
      portraitFallback.textContent = actor.initials;

      portraitImage.addEventListener('load', () => {
        portrait.classList.add('has-image');
      });

      portraitImage.addEventListener('error', () => {
        portrait.classList.remove('has-image');
      });

      portrait.appendChild(portraitImage);
      portrait.appendChild(portraitFallback);

      const name = document.createElement('span');
      name.className = 'story-character-name';
      name.textContent = actor.name;

      card.appendChild(portrait);
      card.appendChild(name);
      card.addEventListener('click', () => onSelect(actor));
      container.appendChild(card);
    });
  }

  window.storyActors = {
    defaultActorId: 'jimmy',
    defaultVoice,
    getActor,
    getAllActors,
    buildStoryUrl,
    renderCharacterCards
  };
})();