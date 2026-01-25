meSpeak.loadConfig("mespeak_config.json");
meSpeak.loadVoice("voices/en/en-us.json");


function speakit(text) 
{
	speakitspeed(text,125);
}

function speakitspeed(text,speed) 
{
  // called by button
  var parts = [{ text: text,
	voice: "en/en-us", 
	variant: "croak",
	amplitude: gameValues["VoiceVolume"]*0.45,
	pitch: 1,
	speed: speed
	}];
  
  meSpeak.speakMultipart(parts);
}